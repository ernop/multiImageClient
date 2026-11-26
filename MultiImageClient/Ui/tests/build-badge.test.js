"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const BuildBadge = require("../wwwroot/build-badge.js");

test("formatRelativeUpdate covers just now, minutes, hours, days, and old dates", () => {
  const now = Date.UTC(2026, 10, 26, 20, 30, 0);
  assert.equal(BuildBadge.formatRelativeUpdate(now - 10_000, now, "en-US"), "updated just now");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 59_999, now, "en-US"), "updated just now");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 60_000, now, "en-US"), "updated 1 minute ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 7 * 60_000, now, "en-US"), "updated 7 minutes ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 59 * 60_000, now, "en-US"), "updated 59 minutes ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 60 * 60_000, now, "en-US"), "updated 1 hour ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 5 * 3_600_000, now, "en-US"), "updated 5 hours ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 24 * 3_600_000, now, "en-US"), "updated 1 day ago");
  assert.equal(BuildBadge.formatRelativeUpdate(now - 12 * 86_400_000, now, "en-US"), "updated 12 days ago");
  const older = BuildBadge.formatRelativeUpdate(now - 45 * 86_400_000, now, "en-US");
  assert.match(older, /^updated [A-Z][a-z]{2} \d{1,2}, 2026$/);
});

test("formatRelativeUpdate handles small clock skew and large future skew honestly", () => {
  const now = Date.UTC(2026, 10, 26, 20, 30, 0);
  assert.equal(BuildBadge.formatRelativeUpdate(now + 30_000, now, "en-US"), "updated just now");
  const farFuture = BuildBadge.formatRelativeUpdate(now + 10 * 60_000, now, "en-US");
  assert.ok(farFuture.startsWith("updated "));
  assert.notEqual(farFuture, "updated just now");
  assert.equal(BuildBadge.formatRelativeUpdate(Number.NaN, now, "en-US"), "");
});

test("resolveUpdatedTimestampMs prefers builtAtUtc over commitDate", () => {
  const built = "2026-11-26T20:23:00Z";
  const commit = "2026-11-20T18:00:00Z";
  assert.equal(
    BuildBadge.resolveUpdatedTimestampMs({ builtAtUtc: built, commitDate: commit }),
    Date.parse(built));
  assert.equal(
    BuildBadge.resolveUpdatedTimestampMs({ builtAtUtc: "", commitDate: commit }),
    Date.parse(commit));
  assert.ok(Number.isNaN(BuildBadge.resolveUpdatedTimestampMs({})));
});
