"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

function loadWorker() {
  const messages = [];
  const self = {
    postMessage(message) {
      messages.push(message);
    },
  };
  const context = vm.createContext({
    self,
    Uint8Array,
    Uint8ClampedArray,
    Float32Array,
    ArrayBuffer,
    Math,
    Error,
    Number,
    Infinity,
  });
  const source = fs.readFileSync(
    path.join(__dirname, "..", "wwwroot", "layout-template-worker.js"),
    "utf8");
  vm.runInContext(source, context, { filename: "layout-template-worker.js" });
  return { self, messages };
}

test("segments a bounded image into the exact requested region count", () => {
  const { self, messages } = loadWorker();
  const width = 24;
  const height = 16;
  const pixels = new Uint8ClampedArray(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const offset = (y * width + x) * 4;
      const left = x < width / 2;
      pixels[offset] = left ? 20 : 225;
      pixels[offset + 1] = left ? 90 : 190;
      pixels[offset + 2] = left ? 220 : 30;
      pixels[offset + 3] = 255;
    }
  }

  self.onmessage({
    data: {
      version: 1,
      requestId: "two-zones",
      width,
      height,
      regionCount: 2,
      data: pixels.buffer,
    },
  });

  assert.equal(messages.length, 1);
  assert.equal(messages[0].requestId, "two-zones");
  assert.equal(messages[0].error, undefined);
  const labels = new Uint8Array(messages[0].labels);
  assert.deepEqual([...new Set(labels)].sort(), [0, 1]);
});

test("rejects region counts outside the product contract", () => {
  const { self, messages } = loadWorker();
  const pixels = new Uint8ClampedArray(2 * 2 * 4);

  self.onmessage({
    data: {
      version: 1,
      requestId: "bad-count",
      width: 2,
      height: 2,
      regionCount: 10,
      data: pixels.buffer,
    },
  });

  assert.match(messages[0].error, /required contract/);
});
