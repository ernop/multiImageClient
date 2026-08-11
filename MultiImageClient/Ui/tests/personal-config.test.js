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
