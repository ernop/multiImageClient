"use strict";

// Page polling runs only while someone uses the tab. After IdleAfterMs without
// input, loops stop scheduling requests; the next input or tab reveal runs every
// resume handler, and the cursor-based polls catch up on what they missed.
// Idle tabs must not keep the socket-activated server awake or stack requests
// against a stalled one. See docs/ui-polling-prd.md.
(function publishTabActivity(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.MultiImageTabActivity = api.create(root, root.document, () => Date.now());
  }
})(typeof globalThis === "object" ? globalThis : this, function createTabActivityModule() {
  const IdleAfterMs = 10 * 60 * 1000;
  // Unfinished own work may extend polling past IdleAfterMs so completion
  // notifications still arrive, but never beyond this long after input.
  const OwnWorkLimitMs = 60 * 60 * 1000;
  const InputEvents = ["pointerdown", "pointermove", "keydown", "wheel", "touchstart", "focus"];

  function create(target, doc, now) {
    let lastInputAt = now();
    let paused = false;
    let ownWorkPending = () => false;
    const resumeHandlers = [];

    function noteInput() {
      lastInputAt = now();
      if (!paused) return;
      paused = false;
      for (const handler of resumeHandlers) handler();
    }

    for (const type of InputEvents) {
      target.addEventListener(type, noteInput, { capture: true, passive: true });
    }
    doc.addEventListener("visibilitychange", () => {
      if (!doc.hidden) noteInput();
    });

    return Object.freeze({
      // Once paused, only input resumes: loops that stopped scheduling must
      // restart together through the resume handlers.
      shouldPoll() {
        if (paused) return false;
        const idleFor = now() - lastInputAt;
        if (idleFor < IdleAfterMs) return true;
        if (idleFor < OwnWorkLimitMs && ownWorkPending()) return true;
        paused = true;
        return false;
      },
      onResume(handler) {
        resumeHandlers.push(handler);
      },
      keepPollingWhile(predicate) {
        ownWorkPending = predicate;
      },
    });
  }

  return Object.freeze({ IdleAfterMs, OwnWorkLimitMs, create });
});
