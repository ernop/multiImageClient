"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { wirePrompt, characterCount, check } = require("../wwwroot/prompt-limits.js");

const recraftV3 = { maxPromptChars: 1000, maxImagePromptChars: 1000, maxPromptUtf8Bytes: null };
const grokApi = { maxPromptChars: 16000, maxImagePromptChars: null, maxPromptUtf8Bytes: null };
const grokWeb = { maxPromptChars: null, maxImagePromptChars: null, maxPromptUtf8Bytes: 8192 };

test("the wire prompt joins the trimmed prompt and extra text with a blank line", () => {
  assert.equal(wirePrompt("  a cat \n", " daylight "), "a cat\n\ndaylight");
  assert.equal(wirePrompt("a cat", "  "), "a cat");
});

test("characters are Unicode code points", () => {
  assert.equal(characterCount("a😀é"), 3);
});

test("the limit is inclusive and counts the extra text and separator", () => {
  const prompt = "a".repeat(996);
  assert.equal(check(recraftV3, prompt, "bb", false), null);
  assert.deepEqual(check(recraftV3, prompt, "bbb", false),
    { action: "fail", count: 1001, max: 1000, unit: "characters" });
  assert.equal(check(recraftV3, "a".repeat(999) + "😀", "", false), null);
});

test("a target receiving the attached image uses its image-route limit", () => {
  const prompt = "a".repeat(16001);
  assert.equal(check(grokApi, prompt, "", false).action, "fail");
  assert.equal(check(grokApi, prompt, "", true), null);
});

test("grok-web truncates by UTF-8 bytes", () => {
  assert.equal(check(grokWeb, "é".repeat(4096), "", false), null);
  assert.deepEqual(check(grokWeb, "é".repeat(4096) + "a", "", false),
    { action: "truncate", count: 8193, max: 8192, unit: "UTF-8 bytes" });
});

test("a target without a published limit is never flagged", () => {
  const unlimited = { maxPromptChars: null, maxImagePromptChars: null, maxPromptUtf8Bytes: null };
  assert.equal(check(unlimited, "a".repeat(100000), "", true), null);
});
