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

test("@Name tags expand once at send time, strip repeated @, and respect possessives", () => {
  const expanded = characters.expandAtTags(
    "@Biscuit's leash while @Jing Li walks beside @Biscuit and @jing.",
    cast);
  assert.equal(expanded.changed, true);
  assert.equal(
    expanded.text,
    "Biscuit's (a small cream Shiba Inu with a red collar) leash while " +
      "Jing Li (a tall man with a gray beard) walks beside Biscuit and " +
      "jing (a woman in her early 30s with round silver glasses).");
  assert.deepEqual(expanded.expanded.map((c) => c.id), ["biscuit", "jing-li", "jing"]);
});

test("@Name ignores email addresses and preserves existing parenthetical descriptions", () => {
  const email = characters.expandAtTags("Send mail to hello@Jing.com right now", cast);
  assert.equal(email.changed, false);
  assert.equal(email.text, "Send mail to hello@Jing.com right now");
  assert.deepEqual(email.expanded, []);

  const already = characters.expandAtTags("@Jing (wearing a space suit) waves to @Jing", cast);
  assert.equal(already.changed, true);
  assert.equal(already.text, "Jing (wearing a space suit) waves to Jing");
  assert.deepEqual(already.expanded.map((c) => c.id), ["jing"]);
});

test("autocompleteContext detects @ prefix, multi-word names, and closes once unrelated text follows", () => {
  const all = characters.autocompleteContext("A portrait of @", 15, cast);
  assert.ok(all);
  assert.equal(all.start, 14);
  assert.equal(all.matches.length, 4);

  const multi = characters.autocompleteContext("Photo of @jing l", 16, cast);
  assert.ok(multi);
  assert.deepEqual(multi.matches.map((c) => c.id), ["jing-li"]);

  const applied = characters.applyAutocomplete("Photo of @jing l", multi.start, multi.end, multi.matches[0]);
  assert.equal(applied.text, "Photo of @Jing Li ");
  assert.equal(applied.caret, applied.text.length);

  assert.equal(characters.autocompleteContext("email@jing", 10, cast), null);
  assert.equal(characters.autocompleteContext("@Jing walks down the street", 27, cast), null);
});

test("insertAtTag inserts compact @Name with word spacing", () => {
  const res = characters.insertAtTag("A photo of", 10, 10, cast, "biscuit");
  assert.equal(res.text, "A photo of @Biscuit ");
  assert.equal(res.caret, res.text.length);
});
