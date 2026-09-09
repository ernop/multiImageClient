const test = require("node:test"), assert = require("node:assert/strict");
require("../../MultiImageClient/Ui/wwwroot/goal-feedback.js");
require("../../MultiImageClient/Ui/wwwroot/goal-tree.js");
require("../../MultiImageClient/Ui/wwwroot/recap-model.js");
test("prompt points share exact text while image samples remain separate", () => {
  const a = { index: 0, text: "Puppy", render: { jobId: "a", generatorKey: "g" } };
  const b = { index: 1, text: "Puppy", render: { jobId: "b", generatorKey: "g" } };
  const entries = [a, b, { feedback: { scope: "prompt", targetEntryIndex: 0, delta: 1 } },
    { feedback: { scope: "prompt", targetEntryIndex: 1, delta: 1 } },
    { feedback: { scope: "image", targetEntryIndex: 0, delta: -1 } }];
  assert.equal(GoalFeedback.total(entries, "prompt", b), 2);
  assert.equal(GoalFeedback.total(entries, "image", a), -1);
  assert.equal(GoalFeedback.total(entries, "image", b), 0);
});
test("sample lineage binds the chosen sibling and its candidate plan", () => {
  const plan = { variant: "c1", title: "Peek", mode: "explore", parents: [] };
  const entries = [{ kind: "design", turn: 1, manager: { parsed: { plan: { candidates: [plan] } } } },
    ...[1, 2].map(s => ({ kind: "render-result", index: s, turn: 1, render: { variant: `c1-s${s}`, source: "A", ok: true } }))];
  const data = GoalTree.collect({ protocolVersion: 9, bestTurn: 1, bestVariant: "c1-s2", bestSource: "A" }, entries);
  assert.equal(data.nodes.length, 2); assert.equal(data.nodes[0].best, false); assert.equal(data.nodes[1].best, true);
  assert.equal(data.nodes[1].title, "Peek"); assert.deepEqual(data.errors, []);
  assert.equal(GoalRecap.variantLabel("c3-s4"), "Candidate 3 · sample 4");
  assert.ok(GoalRecap.variantOrder("c2-s1") > GoalRecap.variantOrder("c1-s4"));
});
