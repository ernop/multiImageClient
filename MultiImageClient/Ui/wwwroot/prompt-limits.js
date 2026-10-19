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

  // Multipart form serialization sends each prompt line break as CR LF, and the
  // server counts the prompt it receives. Extra text travels inside a JSON field
  // and keeps LF.
  function wirePrompt(prompt, extraText) {
    const sentPrompt = prompt.trim().replace(/\r\n|\r|\n/g, "\r\n");
    return [sentPrompt, extraText.trim()].filter(Boolean).join("\n\n");
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

  return Object.freeze({ wirePrompt, characterCount, check });
});
