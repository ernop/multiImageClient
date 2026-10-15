"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const characters = require("../wwwroot/prompt-characters.js");

const cast = [
  { id: "jing", name: "Jing", description: "a woman in her early 30s with round silver glasses" },
  { id: "jing-li", name: "Jing Li", description: "a tall man with a gray beard" },
  { id: "biscuit", name: "Biscuit", description: "a small cream Shiba Inu with a red collar" },
  { id: "court", name: "Grandma's Courtyard", description: "a sunlit red-brick courtyard" },
];

test("mentions are whole words, case-insensitive, and longest name first", () => {
  const text = "Jing Li waves at jing in Beijing. JINGLE bells. Jing_x.";
  const found = characters.findMentions(text, cast);
  assert.deepEqual(found.map((m) => [m.characterId, m.text]), [["jing-li", "Jing Li"], ["jing", "jing"]]);
});

test("possessives match and descriptions go after the suffix", () => {
  const result = characters.describeFirst("Biscuit's ball and Biscuit again", cast, "biscuit");
  assert.equal(result.text, "Biscuit's (a small cream Shiba Inu with a red collar) ball and Biscuit again");
  assert.equal(characters.describeFirst(result.text, cast, "biscuit").changed, false);
});

test("names with apostrophes and spaces match either apostrophe and any spacing", () => {
  const found = characters.findMentions("Lunch in Grandma\u2019s   courtyard today", cast);
  assert.equal(found.length, 1);
  assert.equal(found[0].characterId, "court");
});

test("a parenthesis after a mention counts as the user's own description", () => {
  const text = "Jing (my cousin) and Biscuit";
  const summary = characters.summarize(characters.findMentions(text, cast));
  assert.deepEqual(summary.map((e) => [e.characterId, e.described]), [["jing", true], ["biscuit", false]]);
  const all = characters.describeAll(text, cast);
  assert.equal(all.count, 1);
  assert.equal(all.text, "Jing (my cousin) and Biscuit (a small cream Shiba Inu with a red collar)");
});

test("describe all adds each description once, at each first mention", () => {
  const all = characters.describeAll("Biscuit chases Jing. Jing laughs at Biscuit.", cast);
  assert.equal(all.count, 2);
  assert.equal(all.text,
    "Biscuit (a small cream Shiba Inu with a red collar) chases Jing (a woman in her early 30s with round silver glasses). Jing laughs at Biscuit.");
  assert.equal(characters.describeAll(all.text, cast).changed, false);
});

test("insert places the name at the selection and describes it only once", () => {
  const first = characters.insertCharacter("A sunny beach with", 18, 18, cast, "biscuit");
  assert.equal(first.text, "A sunny beach with Biscuit (a small cream Shiba Inu with a red collar)");
  assert.equal(first.caret, first.text.length);

  const second = characters.insertCharacter(first.text + " and", first.text.length + 4, first.text.length + 4, cast, "biscuit");
  assert.ok(second.text.endsWith(" and Biscuit"));

  const replaced = characters.insertCharacter("hello WORLDdog", 6, 11, cast, "jing");
  assert.equal(replaced.text, "hello Jing (a woman in her early 30s with round silver glasses) dog");
  assert.equal(characters.insertCharacter("x", 0, 0, cast, "missing").changed, false);
});

test("name words feed the spell checker", () => {
  assert.deepEqual(characters.nameWords(cast).sort(), ["biscuit", "courtyard", "grandma's", "jing", "li"]);
});
