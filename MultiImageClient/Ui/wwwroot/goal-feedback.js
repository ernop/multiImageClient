(function (root) {
  "use strict";
  let cachedEntries = null, cachedLength = -1, totals = new Map();
  function targetKey(scope, target) {
    return scope === "prompt" ? `prompt:${target.text}` : `image:${target.render?.jobId}/${target.render?.generatorKey}/0`;
  }
  function total(entries, scope, target) {
    if (!target) return 0;
    // One current-loop metadata cache. Entries append; revision changes replace the array in goal.js.
    if (cachedEntries !== entries || cachedLength !== entries.length) {
      cachedEntries = entries; cachedLength = entries.length; totals = new Map();
      const index = new Map(entries.map(e => [e.index, e]));
      for (const entry of entries) if (entry.feedback) {
        const voted = index.get(entry.feedback.targetEntryIndex);
        if (!voted) throw new Error("Feedback target is missing from this loop.");
        const key = targetKey(entry.feedback.scope, voted);
        totals.set(key, (totals.get(key) || 0) + entry.feedback.delta);
      }
    }
    return totals.get(targetKey(scope, target)) || 0;
  }
  function requestFor(entries, result) {
    const requests = entries.filter(e => e.kind === "render-request" && e.render?.jobId === result.render?.jobId
      && e.render?.variant === result.render?.variant && e.render?.source === result.render?.source && e.turn === result.turn);
    return requests.length === 1 ? requests[0] : null;
  }
  root.GoalFeedback = { total, requestFor };
})(globalThis);
