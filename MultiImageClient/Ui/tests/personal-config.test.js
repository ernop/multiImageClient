"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const schema = require("../wwwroot/personal-config.js");

function fields(values = { name: "ernie", enabled: true }) {
  return [
    {
      name: "creatingAs",
      scope: "browser",
      read: () => values.name,
      normalize: (value) => {
        if (typeof value !== "string") throw new Error("creatingAs must be text");
        return value.trim();
      },
    },
    {
      name: "generatorPreferences",
      scope: "account",
      read: () => ({ enabled: values.enabled }),
      normalize: (value) => {
        if (!value || typeof value.enabled !== "boolean") {
          throw new Error("generatorPreferences are malformed");
        }
        return { enabled: value.enabled };
      },
    },
  ];
}

test("build and normalize round-trip every registered field", () => {
  const registry = fields();
  const document = schema.build(registry);
  assert.deepEqual(schema.normalize(document, registry), document);
  assert.deepEqual(schema.browserFields(registry), ["creatingAs"]);
});

test("rejects missing, unknown, malformed, and duplicate fields", () => {
  const registry = fields();
  const document = schema.build(registry);
  assert.throws(
    () => schema.normalize({ ...document, creatingAs: undefined, surprise: true }, registry),
    /unknown surprise/);
  const missing = { ...document };
  delete missing.creatingAs;
  assert.throws(() => schema.normalize(missing, registry), /missing creatingAs/);
  assert.throws(
    () => schema.normalize({ ...document, generatorPreferences: { enabled: "yes" } }, registry),
    /malformed/);
  assert.throws(
    () => schema.assertRegistry([...registry, registry[0]]),
    /duplicate creatingAs/);
});

test("rejects unsupported versions unless an exact migration is supplied", () => {
  const registry = fields();
  const old = { ...schema.build(registry), version: 1, oldName: "ernie" };
  delete old.creatingAs;
  assert.throws(() => schema.normalize(old, registry), /unsupported configuration version 1/);
  const migrated = schema.normalize(old, registry, (source, targetVersion) => {
    assert.equal(targetVersion, schema.Version);
    const { oldName, ...rest } = source;
    return { ...rest, version: targetVersion, creatingAs: oldName };
  });
  assert.equal(migrated.creatingAs, "ernie");
});

test("stored document parsing fails closed", () => {
  assert.equal(schema.parseStored(null), null);
  assert.throws(() => schema.parseStored("{"), /not valid JSON/);
  assert.throws(
    () => schema.parseStored(JSON.stringify({ format: schema.Format, version: 99 })),
    /unsupported/);
});

test("version 2 migration preserves endpoint settings and declares empty global directives", () => {
  const old = { format: schema.Format, version: 2,
    promptTools: { claudeAdviceInstruction: "Keep my instruction" },
    generatorPreferences: { hiddenGeneratorKeys: [], defaultSelectedKeys: ["gpt2"],
      endpointConfigurations: [{ key: "gpt2", extraText: "", notes: "Private" }] } };
  const migrated = schema.parseStored(JSON.stringify(old));
  assert.equal(migrated.version, schema.Version);
  assert.deepEqual(migrated.promptTools, { claudeAdviceInstruction: "Keep my instruction", globalAppendText: "" });
  assert.deepEqual(migrated.generatorPreferences,
    { ...old.generatorPreferences, defaultSelectedKeys: ["gpt2", "ideogram-v45"] });
  assert.equal(old.version, 2);
  assert.equal(schema.migrateVersion2(old).version, 3);
  assert.throws(() => schema.migrateVersion2({ ...old, promptTools: { ...old.promptTools, surprise: true } }), /invalid/);
  assert.throws(() => schema.migrateVersion2({ ...old, promptTools: {} }), /invalid/);
});

function version3Document(generatorPreferences) {
  return { format: schema.Format, version: 3, creatingAs: "ernie",
    promptTools: { claudeAdviceInstruction: "Keep my instruction", globalAppendText: "" },
    generatorPreferences };
}

test("version 3 migration adds Ideogram 4.5 once to the saved default list", () => {
  const old = version3Document({ defaultView: "only-sota", hiddenGeneratorKeys: ["recraft"],
    defaultSelectedKeys: ["gpt2", "ideogram"], presets: [], endpointConfigurations: [] });
  const migrated = schema.parseStored(JSON.stringify(old));
  assert.equal(migrated.version, 4);
  assert.deepEqual(migrated, { ...old, version: 4,
    generatorPreferences: { ...old.generatorPreferences, defaultSelectedKeys: ["gpt2", "ideogram", "ideogram-v45"] } });
  assert.deepEqual(old.generatorPreferences.defaultSelectedKeys, ["gpt2", "ideogram"]);
  const empty = schema.migrateVersion3(version3Document({ hiddenGeneratorKeys: [], defaultSelectedKeys: [] }));
  assert.deepEqual(empty.generatorPreferences.defaultSelectedKeys, ["ideogram-v45"]);
});

test("version 3 migration leaves a present or hidden Ideogram 4.5 unchanged", () => {
  const present = { hiddenGeneratorKeys: [], defaultSelectedKeys: ["ideogram-v45", "gpt2"] };
  assert.deepEqual(schema.migrateVersion3(version3Document(present)).generatorPreferences, present);
  const hidden = { hiddenGeneratorKeys: ["ideogram-v45"], defaultSelectedKeys: ["gpt2"] };
  assert.deepEqual(schema.migrateVersion3(version3Document(hidden)).generatorPreferences, hidden);
});

test("a removal after the version 4 migration stays removed", () => {
  const storage = memoryStorage([[schema.StorageKey, JSON.stringify(version3Document(
    { hiddenGeneratorKeys: [], defaultSelectedKeys: ["gpt2"] }))]]);
  const migrated = schema.parseStored(storage.getItem(schema.StorageKey));
  assert.deepEqual(migrated.generatorPreferences.defaultSelectedKeys, ["gpt2", "ideogram-v45"]);
  schema.saveGeneratorPreferences(storage, { hiddenGeneratorKeys: [], defaultSelectedKeys: ["gpt2"] });
  const saved = schema.parseStored(storage.getItem(schema.StorageKey));
  assert.equal(saved.version, 4);
  assert.deepEqual(saved.generatorPreferences.defaultSelectedKeys, ["gpt2"]);
});

test("version 3 migration rejects documents without default or hidden lists", () => {
  for (const preferences of [undefined, [], { defaultSelectedKeys: ["gpt2"] }, { hiddenGeneratorKeys: [] }]) {
    assert.throws(() => schema.parseStored(JSON.stringify(version3Document(preferences))), /lack the default or hidden list/);
  }
  assert.throws(() => schema.migrateVersion3({ ...version3Document({ hiddenGeneratorKeys: [], defaultSelectedKeys: [] }),
    version: 2 }), /invalid version 3/);
  assert.throws(() => schema.migrateVersion3({ ...version3Document({ hiddenGeneratorKeys: [], defaultSelectedKeys: [] }),
    format: "other" }), /invalid version 3/);
});

function memoryStorage(entries = []) {
  const values = new Map(entries);
  return {
    getItem: (key) => values.has(key) ? values.get(key) : null,
    setItem: (key, value) => values.set(key, value),
  };
}

test("chooser saves preserve unrelated canonical fields and ignore obsolete legacy values", () => {
  const original = schema.build(fields());
  const storage = memoryStorage([
    [schema.StorageKey, JSON.stringify(original)],
    ["mic_ui_settings_v1", "malformed obsolete value"],
  ]);
  schema.saveGeneratorPreferences(storage, { enabled: false });
  const saved = schema.parseStored(storage.getItem(schema.StorageKey));
  assert.deepEqual(saved, { ...original, generatorPreferences: { enabled: false } });
  assert.deepEqual(schema.normalize(saved, fields()), saved);
});

test("chooser saves preserve the complete spelling checker configuration", () => {
  const original = schema.build(fields());
  original.spelling = {
    enabled: true, customDictionary: ["myword"], ignoredWords: ["name"], notRareWords: ["term"],
    formality: "standard", ruleOverrides: {
      rules: { culture: false }, params: { obscureRank: 12000 },
      checkers: { spell2026: { enabled: false }, spell: { enabled: true, order: 0 },
        echo: { order: 9, params: { echoWindowWords: 7 } } },
    },
  };
  const storage = memoryStorage([[schema.StorageKey, JSON.stringify(original)]]);
  schema.saveGeneratorPreferences(storage, { enabled: false });
  const saved = schema.parseStored(storage.getItem(schema.StorageKey));
  assert.deepEqual(saved.spelling, original.spelling);
  assert.deepEqual(saved.generatorPreferences, { enabled: false });
});

test("first chooser save preserves legacy browser preferences in the complete document", () => {
  const storage = memoryStorage([
    ["mic_username", "Alice"],
    ["mic_user_filter_v1", '["Bob"]'],
    ["mic_ui_settings_v1", '{"showCosts":true,"activitySound":true}'],
    ["mic_spellwell_custom_dict", '["specialword"]'],
    ["mic_video_audio_v1", '{"volume":0.3,"muted":true}'],
  ]);
  schema.saveGeneratorPreferences(storage, { enabled: true });
  const saved = schema.parseStored(storage.getItem(schema.StorageKey));
  assert.equal(saved.creatingAs, "Alice");
  assert.deepEqual(saved.peopleFilters, ["Bob"]);
  assert.equal(saved.uiSettings.showCosts, true);
  assert.equal(saved.uiSettings.activitySound, true);
  assert.deepEqual(saved.spelling.customDictionary, ["specialword"]);
  assert.deepEqual(saved.videoAudio, { volume: 0.3, muted: true });
  assert.deepEqual(Object.keys(saved).sort(), [
    "format", "version", "creatingAs", "peopleFilters", "generatorPreferences", "uiSettings",
    "promptTools", "spelling", "inspirationLibrary", "viewer", "videoAudio", "costSummaryCollapsed",
  ].sort());
});

test("chooser saves reject malformed or unsupported canonical documents without replacing them", () => {
  for (const raw of ['{', JSON.stringify({format:schema.Format,version:99})]) {
    const storage = memoryStorage([[schema.StorageKey, raw]]);
    assert.throws(() => schema.saveGeneratorPreferences(storage, { enabled: true }));
    assert.equal(storage.getItem(schema.StorageKey), raw);
  }
});
