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
