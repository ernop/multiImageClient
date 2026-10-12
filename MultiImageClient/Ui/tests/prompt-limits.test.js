"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const {
  asSubmittedFormText,
  countCharacters,
  countUtf8Bytes,
  measure,
  measureWirePrompt,
  evaluate,
} = require("../wwwroot/prompt-limits.js");

const OpenAiLimit = Object.freeze({ max: 32000, unit: "characters", policy: "reject", textToImageOnly: false });
const IdeogramV45Limit = Object.freeze({ max: 10000, unit: "characters", policy: "reject", textToImageOnly: false });
const GrokWebLimit = Object.freeze({ max: 8192, unit: "utf8Bytes", policy: "truncate", textToImageOnly: true });
const TextToImage = Object.freeze({ runsTextToImage: true });

test("multipart form serialization sends every line break as CRLF", async () => {
  const form = new FormData();
  form.append("prompt", "a\nb\rc\r\nd");
  const body = await new Response(form).text();
  const value = body.match(/name="prompt"\r\n\r\n([\s\S]*?)\r\n--/)[1];
  assert.equal(value, "a\r\nb\r\nc\r\nd");
  assert.equal(asSubmittedFormText("a\nb\rc\r\nd"), value);
});

test("the wire prompt is the trimmed prompt, then trimmed extra text after a blank line", () => {
  const wire = (prompt, extraText) => {
    const submitted = asSubmittedFormText(prompt.trim());
    return extraText.trim() ? `${submitted}\n\n${extraText.trim()}` : submitted;
  };
  const cases = [
    [" prompt ", ""],
    ["prompt", "  \n "],
    ["prompt", "\n extra \n"],
    ["two\nlines", "extra\nlines"],
    ["", "extra"],
    ["界😀\n", "é\ud83d"],
  ];
  for (const [prompt, extraText] of cases) {
    for (const unit of ["characters", "utf8Bytes"]) {
      assert.equal(
        measureWirePrompt(prompt, extraText, unit),
        measure(wire(prompt, extraText), unit),
        `${JSON.stringify([prompt, extraText])} in ${unit}`);
    }
  }
});

test("a changed prompt is measured again", () => {
  assert.equal(measureWirePrompt("abc", "", "characters"), 3);
  assert.equal(measureWirePrompt("abcd", "", "characters"), 4);
  assert.equal(measureWirePrompt("abcd", "", "utf8Bytes"), 4);
  assert.equal(measureWirePrompt("界", "", "utf8Bytes"), 3);
  assert.equal(measureWirePrompt("界", "", "characters"), 1);
});

test("characters are code points and bytes are UTF-8, as on the server", () => {
  const samples = ["", "a", "é", "界", "😀", "a😀b", "\ud83d", "\ude00x", "x\ud83d"];
  const expectedCharacters = [0, 1, 1, 1, 1, 3, 1, 2, 2];
  samples.forEach((sample, index) => {
    assert.equal(countCharacters(sample), expectedCharacters[index], JSON.stringify(sample));
    assert.equal(countUtf8Bytes(sample), new TextEncoder().encode(sample).length, JSON.stringify(sample));
  });
  assert.equal(measure("😀", "characters"), 1);
  assert.equal(measure("😀", "utf8Bytes"), 4);
  assert.throws(() => measure("a", "words"), /Unknown prompt-limit unit 'words'/);
});

test("the 2026-10-12 production prompt is over the GPT Image and Ideogram 4.5 limits", () => {
  const prompt = "a".repeat(34874);
  const gptExtraText = "b".repeat(214);
  assert.deepEqual(evaluate(OpenAiLimit, prompt, gptExtraText, TextToImage), {
    measured: 35090,
    max: 32000,
    unit: "characters",
    policy: "reject",
    includesExtraText: true,
  });
  assert.deepEqual(evaluate(IdeogramV45Limit, prompt, "", TextToImage), {
    measured: 34874,
    max: 10000,
    unit: "characters",
    policy: "reject",
    includesExtraText: false,
  });
});

test("a prompt exactly at the limit passes; one more character is over", () => {
  assert.equal(evaluate(IdeogramV45Limit, "a".repeat(10000), "", TextToImage), null);
  assert.equal(evaluate(IdeogramV45Limit, "a".repeat(9990), "b".repeat(8), TextToImage), null);
  assert.equal(evaluate(IdeogramV45Limit, "a".repeat(9990), "b".repeat(9), TextToImage).measured, 10001);
  assert.equal(evaluate(IdeogramV45Limit, ` ${"a".repeat(10000)}\n`, "", TextToImage), null);
});

test("prompt line breaks count twice because the form sends CRLF", () => {
  const limit = { max: 4, unit: "characters", policy: "reject", textToImageOnly: false };
  assert.equal(evaluate(limit, "a\nb", "", TextToImage), null);
  assert.equal(evaluate({ ...limit, max: 3 }, "a\nb", "", TextToImage).measured, 4);
  assert.equal(evaluate({ ...limit, max: 6 }, "a", "b\nc", TextToImage), null);
});

test("a text-to-image-only limit does not apply when the target sends the image", () => {
  const prompt = "😀".repeat(2049);
  assert.equal(evaluate(GrokWebLimit, prompt, "", { runsTextToImage: false }), null);
  assert.deepEqual(evaluate(GrokWebLimit, prompt, "", TextToImage), {
    measured: 8196,
    max: 8192,
    unit: "utf8Bytes",
    policy: "truncate",
    includesExtraText: false,
  });
});

test("targets without a published limit are never over", () => {
  assert.equal(evaluate(null, "a".repeat(100000), "", TextToImage), null);
});
