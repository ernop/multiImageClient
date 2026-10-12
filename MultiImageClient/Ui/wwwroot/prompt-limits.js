"use strict";

// Prompt-length limits published per generator by /api/config as
// `promptLimit` (server: UiPromptLimits.Publish). The composer measures the
// exact text each target receives, as the server does, so a prompt that a
// provider rejects never reaches Generate. See docs/prompt-length-limits.md.
(function publishPromptLimits(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImagePromptLimits = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createPromptLimitsModule() {
  // multipart/form-data serialization sends every textarea line break as
  // CRLF, so the server and the provider count two characters for each one.
  // Endpoint extra texts travel inside a JSON field and keep LF.
  function asSubmittedFormText(text) {
    return text.replace(/\r\n|\r|\n/g, "\r\n");
  }

  function isHighSurrogate(unit) {
    return unit >= 0xd800 && unit <= 0xdbff;
  }

  function isLowSurrogate(unit) {
    return unit >= 0xdc00 && unit <= 0xdfff;
  }

  // These counters run on every keystroke for every selected target, so they
  // walk the string instead of allocating an encoded copy. A lone surrogate
  // counts as one character and three bytes (U+FFFD), as on the server.
  function countCharacters(text) {
    let count = 0;
    for (let i = 0; i < text.length; i += 1) {
      if (isHighSurrogate(text.charCodeAt(i)) && isLowSurrogate(text.charCodeAt(i + 1))) {
        i += 1;
      }
      count += 1;
    }
    return count;
  }

  function countUtf8Bytes(text) {
    let count = 0;
    for (let i = 0; i < text.length; i += 1) {
      const unit = text.charCodeAt(i);
      if (unit < 0x80) {
        count += 1;
      } else if (unit < 0x800) {
        count += 2;
      } else if (isHighSurrogate(unit) && isLowSurrogate(text.charCodeAt(i + 1))) {
        count += 4;
        i += 1;
      } else {
        count += 3;
      }
    }
    return count;
  }

  function measure(text, unit) {
    switch (unit) {
      case "characters": return countCharacters(text);
      case "utf8Bytes": return countUtf8Bytes(text);
      default: throw new Error(`Unknown prompt-limit unit '${unit}'.`);
    }
  }

  // One composer update measures the same prompt for every target, so the
  // counts of the most recent prompt are kept.
  let lastPrompt = null;
  let lastSubmittedPrompt = "";
  const lastPromptCounts = new Map();

  function measureSubmittedPrompt(prompt, unit) {
    if (prompt !== lastPrompt) {
      lastPrompt = prompt;
      lastSubmittedPrompt = asSubmittedFormText(prompt.trim());
      lastPromptCounts.clear();
    }
    if (!lastPromptCounts.has(unit)) {
      lastPromptCounts.set(unit, measure(lastSubmittedPrompt, unit));
    }
    return lastPromptCounts.get(unit);
  }

  // The server sends the trimmed prompt, then any nonblank extra text after
  // "\n\n" (UiPromptLimits.ComposeWirePrompt). Both units add across that
  // join, because the separator never splits a character.
  function measureWirePrompt(prompt, extraText, unit) {
    const extra = extraText.trim();
    return measureSubmittedPrompt(prompt, unit) + (extra ? measure(`\n\n${extra}`, unit) : 0);
  }

  // Returns the overrun for one target, or null when it receives the whole
  // prompt. `runsTextToImage` is false when the target sends an attached
  // image through a path that the limit does not cover.
  function evaluate(limit, prompt, extraText, { runsTextToImage }) {
    if (!limit || (limit.textToImageOnly && !runsTextToImage)) return null;
    const measured = measureWirePrompt(prompt, extraText, limit.unit);
    if (measured <= limit.max) return null;
    return {
      measured,
      max: limit.max,
      unit: limit.unit,
      policy: limit.policy,
      includesExtraText: extraText.trim() !== "",
    };
  }

  return Object.freeze({
    asSubmittedFormText,
    countCharacters,
    countUtf8Bytes,
    measure,
    measureWirePrompt,
    evaluate,
  });
});
