"use strict";

// Measures each target's wire prompt the way the server checks it
// (UiJobRunner.DescribePromptLimitProblem): the trimmed prompt, a blank line,
// and the endpoint extra text, counted in Unicode characters. Over
// maxPromptChars (or maxImagePromptChars when the target receives the attached
// image) the server fails the target without sending it. maxPromptUtf8Bytes is
// grok-web's text-to-image truncation budget; its edits send the full prompt.
// See docs/prompt-length-limits.md.
(function publishPromptLimits(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImagePromptLimits = api;
  }
})(typeof globalThis === "object" ? globalThis : this, function createPromptLimitsModule() {
  const encoder = new TextEncoder();

  // Multipart form serialization sends each prompt line break as CR LF; the
  // server stores LF (UiJobRunner.PromptFromFormField), so a line break counts
  // as one character. Extra text travels inside a JSON field and keeps LF.
  function sentPrompt(prompt) {
    return prompt.replace(/\r\n?/g, "\n").trim();
  }

  function wirePrompt(prompt, extraText) {
    return [sentPrompt(prompt), extraText.trim()].filter(Boolean).join("\n\n");
  }

  function characterCount(text) {
    let count = 0;
    for (const _ of text) count += 1;
    return count;
  }

  // Null within the target's limit; otherwise what happens to the prompt.
  function check(generator, prompt, extraText, imageSent) {
    const wire = wirePrompt(prompt, extraText);
    if (generator.maxPromptUtf8Bytes) {
      if (imageSent) return null;
      const bytes = encoder.encode(wire).length;
      return bytes > generator.maxPromptUtf8Bytes
        ? { action: "truncate", count: bytes, max: generator.maxPromptUtf8Bytes, unit: "UTF-8 bytes" }
        : null;
    }
    const max = imageSent ? generator.maxImagePromptChars : generator.maxPromptChars;
    if (!max) return null;
    const count = characterCount(wire);
    return count > max ? { action: "fail", count, max, unit: "characters" } : null;
  }

  // The longest prompt that fits the target's limit after its extra text
  // (UiJobRunner.PlanPromptCondense), or null without a character limit.
  function condenseBudget(generator, extraText, imageSent) {
    if (generator.maxPromptUtf8Bytes) return null;
    const max = imageSent ? generator.maxImagePromptChars : generator.maxPromptChars;
    if (!max) return null;
    const extra = extraText.trim();
    return max - (extra ? 2 + characterCount(extra) : 0);
  }

  // A condensation ({ sourcePrompt, text }) replaces the prompt for a target
  // only while the prompt still equals its source, the full prompt is over
  // the target's limit, and the condensed prompt fits. Null otherwise.
  function condensedCheck(generator, prompt, extraText, imageSent, condensation) {
    if (!condensation || condensation.sourcePrompt !== sentPrompt(prompt)) return null;
    const full = check(generator, prompt, extraText, imageSent);
    if (!full || full.action !== "fail") return null;
    const count = characterCount(wirePrompt(condensation.text, extraText));
    return count <= full.max ? { count, max: full.max, fullCount: full.count } : null;
  }

  return Object.freeze({ sentPrompt, wirePrompt, characterCount, check, condenseBudget, condensedCheck });
});
