"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const {
  sentPrompt, wirePrompt, characterCount, check, condenseBudget, condensedCheck,
} = require("../wwwroot/prompt-limits.js");

const recraftV3 = { maxPromptChars: 1000, maxImagePromptChars: 1000, maxPromptUtf8Bytes: null };
const grokApi = { maxPromptChars: 16000, maxImagePromptChars: null, maxPromptUtf8Bytes: null };
const grokWeb = { maxPromptChars: null, maxImagePromptChars: null, maxPromptUtf8Bytes: 8192 };

test("the wire prompt joins the trimmed prompt and extra text with a blank line", () => {
  assert.equal(wirePrompt("  a cat \n", " daylight "), "a cat\n\ndaylight");
  assert.equal(wirePrompt("a cat", "  "), "a cat");
});

test("prompt line breaks become LF, as the server stores them", () => {
  assert.equal(wirePrompt("a\nb\r\nc\rd\r\n", "e\nf"), "a\nb\nc\nd\n\ne\nf");
});

test("characters are Unicode code points", () => {
  assert.equal(characterCount("a😀é"), 3);
});

test("each line break counts one character", () => {
  const prompt = Array(500).fill("a").join("\n");
  assert.equal(check(recraftV3, prompt, "", false), null);
  assert.deepEqual(check(recraftV3, prompt + "\r\nbc", "", false),
    { action: "fail", count: 1002, max: 1000, unit: "characters" });
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

test("grok-web truncates text-to-image prompts by UTF-8 bytes", () => {
  assert.equal(check(grokWeb, "é".repeat(4096), "", false), null);
  assert.deepEqual(check(grokWeb, "é".repeat(4096) + "a", "", false),
    { action: "truncate", count: 8193, max: 8192, unit: "UTF-8 bytes" });
});

test("grok-web edits send the full prompt", () => {
  assert.equal(check(grokWeb, "a".repeat(20000), "", true), null);
});

test("a target without a published limit is never flagged", () => {
  const unlimited = { maxPromptChars: null, maxImagePromptChars: null, maxPromptUtf8Bytes: null };
  assert.equal(check(unlimited, "a".repeat(100000), "", true), null);
});

test("the sent prompt is trimmed with LF line breaks", () => {
  assert.equal(sentPrompt("  a\r\nb\rc \n"), "a\nb\nc");
});

test("the condense budget leaves room for the extra text and its separator", () => {
  assert.equal(condenseBudget(recraftV3, "", false), 1000);
  assert.equal(condenseBudget(recraftV3, "  daylight  ", false), 1000 - 2 - 8);
  assert.equal(condenseBudget(grokApi, "", false), 16000);
  assert.equal(condenseBudget(grokApi, "", true), null);
});

test("grok-web has no condense budget, because it truncates instead", () => {
  assert.equal(condenseBudget(grokWeb, "", false), null);
  assert.equal(condensedCheck(grokWeb, "a".repeat(9000), "", false,
    { sourcePrompt: "a".repeat(9000), text: "a" }), null);
});

test("a condensed prompt applies while its source matches and it fits", () => {
  const prompt = "a".repeat(1200);
  const condensation = { sourcePrompt: prompt, text: "b".repeat(990) };
  assert.deepEqual(condensedCheck(recraftV3, ` ${prompt}\n`, "cccccccc", false, condensation),
    { count: 1000, max: 1000, fullCount: 1210 });
  assert.equal(condensedCheck(recraftV3, prompt, "ccccccccc", false, condensation), null);
});

test("a condensed prompt stops applying when the prompt changes", () => {
  const condensation = { sourcePrompt: "a".repeat(1200), text: "b".repeat(500) };
  assert.equal(condensedCheck(recraftV3, "a".repeat(1201), "", false, condensation), null);
  assert.equal(condensedCheck(recraftV3, "a".repeat(1200), "", false, null), null);
});

test("a condensed prompt is unused while the full prompt fits", () => {
  const condensation = { sourcePrompt: "a".repeat(900), text: "b".repeat(500) };
  assert.equal(condensedCheck(recraftV3, "a".repeat(900), "", false, condensation), null);
  const imageRoute = { sourcePrompt: "a".repeat(16001), text: "b".repeat(500) };
  assert.equal(condensedCheck(grokApi, "a".repeat(16001), "", true, imageRoute), null);
});
