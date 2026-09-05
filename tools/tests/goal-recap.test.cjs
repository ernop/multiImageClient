const test = require("node:test"),
  assert = require("node:assert/strict");
require("../../MultiImageClient/Ui/wwwroot/recap-model.js");
const loop = {
  id: "test",
  protocolVersion: 6,
  generators: [{ key: "one", label: "Maker", source: "A" }],
  managerLabel: "Manager",
  critics: [{ index: 0, label: "Manager" }],
};
const render = (index, turn, variant) => [
  {
    index,
    kind: "render-request",
    turn,
    text: "Exact prompt",
    render: { jobId: `j${index}`, variant, source: "A" },
  },
  {
    index: index + 1,
    kind: "render-result",
    turn,
    render: {
      jobId: `j${index}`,
      variant,
      source: "A",
      generatorKey: "one",
      generatorLabel: "Maker",
      ok: true,
      imageUrl: `https://images.example/${index}.png`,
      thumbUrl: `/thumb/${index}`,
    },
  },
];
const rows = ["refine", "fresh"].map((variant) => ({
  variant,
  source: "A",
  evaluation: { score: 8, assessment: "A model judgment." },
}));
const entries = [
  ...render(1, 1, "refine"),
  ...render(3, 1, "fresh"),
  {
    index: 5,
    turn: 1,
    kind: "review",
    text: "reply",
    manager: { parsed: { renderEvaluations: rows } },
  },
  ...render(6, 2, "refine"),
];
test("preserves ties and leaves latest unreviewed", () => {
  const d = GoalRecap.collect(loop, entries, (x) => x);
  assert.deepEqual(d.cuts.best, ["2", "4"]);
  assert.deepEqual(d.cuts.latest, ["7"]);
  assert.equal(d.items[2].score, null);
  assert.equal(d.items[0].prompt, "Exact prompt");
  assert.equal(d.participants[1].role, "Critic 1");
});
test("does not accept a failed reply as evidence", () => {
  const es = structuredClone(entries);
  es[4].error = "Malformed reply";
  const d = GoalRecap.collect(loop, es, (x) => x);
  assert.deepEqual(d.cuts.best, []);
  assert.equal(d.contributions[0].error, true);
});
test("rejects ambiguous render requests and missing scores", () => {
  assert.throws(
    () => GoalRecap.collect(loop, [...entries, entries[0]], (x) => x),
    /exact request/,
  );
  const es = structuredClone(entries);
  es[4].manager.parsed.renderEvaluations.pop();
  assert.throws(() => GoalRecap.collect(loop, es, (x) => x), /missing score/);
});
test("legacy paired loops retain null source identity", () => {
  const l = {
    ...loop,
    protocolVersion: 4,
    generators: [],
    generatorKey: "one",
    generatorLabel: "Maker",
  };
  const es = structuredClone(entries);
  for (const e of es) if (e.render) delete e.render.source;
  es[4].manager.parsed = {
    evaluation: rows[0].evaluation,
    freshEvaluation: rows[1].evaluation,
  };
  assert.deepEqual(GoalRecap.collect(l, es, (x) => x).cuts.best, ["2", "4"]);
});
