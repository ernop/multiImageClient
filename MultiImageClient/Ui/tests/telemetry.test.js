"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Telemetry = require("../wwwroot/telemetry.js");

test("sanitizeData strips forbidden keys, nested values, and trims strings", () => {
  const cleaned = Telemetry.sanitizeData({
    action: "render",
    genCount: 3,
    autoRender: true,
    prompt: "never send user prompts",
    instruction: "never send instructions",
    secretToken: "abc",
    ip: "127.0.0.1",
    nested: { a: 1 },
    longLabel: "x".repeat(120),
    badNum: Number.NaN,
  });
  assert.deepEqual(cleaned, {
    action: "render",
    genCount: 3,
    autoRender: true,
    longLabel: "x".repeat(80),
  });
});

test("createClient batches events, skips flush when empty, and sends valid JSON", async () => {
  const sent = [];
  const client = Telemetry.createClient({
    page: "composer",
    apiUrl: (p) => `/sub/${p}`,
    fetchImpl: async (url, init) => {
      sent.push({ url, init, body: JSON.parse(init.body) });
      return { ok: true, status: 200 };
    },
  });

  assert.equal(await client.flush(), false);
  assert.equal(sent.length, 0);

  assert.equal(client.track("stage1.mode", { mode: "split" }), true);
  assert.equal(client.track("BAD EVENT!", {}), false);
  assert.equal(client.pendingCount(), 1);

  assert.equal(await client.flush({ keepalive: true }), true);
  assert.equal(client.pendingCount(), 0);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].url, "/sub/api/telemetry");
  assert.equal(sent[0].init.keepalive, true);
  assert.equal(sent[0].body.page, "composer");
  assert.match(sent[0].body.tab, /^[a-zA-Z0-9_-]{4,32}$/);
  assert.deepEqual(sent[0].body.events, [{ ev: "stage1.mode", seq: 0, d: { mode: "split" } }]);
});

test("viewportBucket classifies phone, tablet, and desktop widths", () => {
  assert.equal(Telemetry.viewportBucket(390), "phone");
  assert.equal(Telemetry.viewportBucket(820), "tablet");
  assert.equal(Telemetry.viewportBucket(1440), "desktop");
});
