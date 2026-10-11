"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { IdleAfterMs, OwnWorkLimitMs, create } = require("../wwwroot/tab-activity.js");

function harness() {
  const clock = { now: 1_000_000 };
  const target = new EventTarget();
  const doc = new EventTarget();
  doc.hidden = false;
  const activity = create(target, doc, () => clock.now);
  let resumed = 0;
  activity.onResume(() => { resumed += 1; });
  return { clock, target, doc, activity, resumes: () => resumed };
}

function pause({ clock, activity }) {
  clock.now += IdleAfterMs;
  assert.equal(activity.shouldPoll(), false);
}

test("polls until the idle limit, then stops", () => {
  const { clock, activity } = harness();
  assert.equal(activity.shouldPoll(), true);
  clock.now += IdleAfterMs - 1;
  assert.equal(activity.shouldPoll(), true);
  clock.now += 1;
  assert.equal(activity.shouldPoll(), false);
});

test("input restarts the idle limit without a resume", () => {
  const h = harness();
  h.clock.now += IdleAfterMs - 1;
  h.target.dispatchEvent(new Event("pointermove"));
  h.clock.now += IdleAfterMs - 1;
  assert.equal(h.activity.shouldPoll(), true);
  assert.equal(h.resumes(), 0);
});

test("every input kind resumes a paused tab once", () => {
  for (const type of ["pointerdown", "pointermove", "keydown", "wheel", "touchstart", "focus"]) {
    const h = harness();
    pause(h);
    h.target.dispatchEvent(new Event(type));
    assert.equal(h.resumes(), 1, type);
    assert.equal(h.activity.shouldPoll(), true, type);
    h.target.dispatchEvent(new Event(type));
    assert.equal(h.resumes(), 1, `${type} while polling is not a resume`);
  }
});

test("every resume handler runs", () => {
  const h = harness();
  let second = 0;
  h.activity.onResume(() => { second += 1; });
  pause(h);
  h.target.dispatchEvent(new Event("keydown"));
  assert.equal(h.resumes(), 1);
  assert.equal(second, 1);
});

test("an idle period no loop observed needs no resume", () => {
  const h = harness();
  h.clock.now += IdleAfterMs * 3;
  h.target.dispatchEvent(new Event("pointerdown"));
  assert.equal(h.resumes(), 0);
  assert.equal(h.activity.shouldPoll(), true);
});

test("showing the tab resumes; hiding it does not", () => {
  const h = harness();
  pause(h);
  h.doc.hidden = true;
  h.doc.dispatchEvent(new Event("visibilitychange"));
  assert.equal(h.resumes(), 0);
  assert.equal(h.activity.shouldPoll(), false);
  h.doc.hidden = false;
  h.doc.dispatchEvent(new Event("visibilitychange"));
  assert.equal(h.resumes(), 1);
  assert.equal(h.activity.shouldPoll(), true);
});

test("unfinished own work extends polling up to its limit", () => {
  const h = harness();
  h.activity.keepPollingWhile(() => true);
  h.clock.now += OwnWorkLimitMs - 1;
  assert.equal(h.activity.shouldPoll(), true);
  h.clock.now += 1;
  assert.equal(h.activity.shouldPoll(), false);
});

test("polling stops when own work finishes after the idle limit, and stays stopped", () => {
  const h = harness();
  let working = true;
  h.activity.keepPollingWhile(() => working);
  h.clock.now += IdleAfterMs;
  assert.equal(h.activity.shouldPoll(), true);
  working = false;
  assert.equal(h.activity.shouldPoll(), false);
  working = true;
  assert.equal(h.activity.shouldPoll(), false, "only input resumes a paused tab");
  h.target.dispatchEvent(new Event("pointerdown"));
  assert.equal(h.resumes(), 1);
  assert.equal(h.activity.shouldPoll(), true);
});
