const test = require("node:test"), assert = require("node:assert/strict");
require("../../MultiImageClient/Ui/wwwroot/goal-tree.js");
const loop = { protocolVersion: 8, bestTurn: 2, bestVariant: "c3", bestSource: "A" };
const ref = (turn, variant, source = "A") => ({ turn, variant, source, contribution: "Keep the palette." });
const plan = (variant, parents = []) => ({ variant, title: `Idea ${variant}`, mode: "explore", prompt: "Exact prompt", parents });
const design = (turn, candidates, candidateDecisions = []) => ({ turn, kind: turn === 1 ? "design" : "review",
  manager: { parsed: { plan: { candidates }, candidateDecisions } } });
const render = (turn, variant, source = "A", ok = true) => ({ index: turn * 10 + Number(variant.slice(1)), turn, kind: "render-result",
  render: { variant, source, ok, thumbUrl: `/thumb/${turn}/${variant}/${source}` } });
test("three candidate slots are separate render nodes with exact source parents", () => {
  const es = [design(1, [plan("c1"), plan("c2"), plan("c3")]), render(1, "c1"), render(1, "c1", "B"), render(1, "c2"), render(1, "c3"),
    { ...design(2, [plan("c1", [ref(1, "c1", "B")]), plan("c3", [ref(1, "c1"), ref(1, "c3")])],
      [{ variant: "c1", action: "branch", reason: "Two useful palettes." }, { variant: "c2", action: "drop" }]), turn: 1 },
    { turn: 2, kind: "render-request", render: { variant: "c3", source: "A" }, text: "Actual edited prompt" },
    render(2, "c3"), render(2, "c1")];
  const data = GoalTree.collect(loop, es);
  assert.equal(data.nodes.length, 6);
  assert.deepEqual(data.edges.map(e => `${e.from}->${e.to}`), ["1/c1/B->2/c1/A", "1/c1/A->2/c3/A", "1/c3/A->2/c3/A"]);
  assert.equal(data.nodes[2].action, "drop");
  assert.equal(data.nodes.at(-1).best, true);
  assert.equal(data.nodes.at(-1).prompt, "Actual edited prompt");
  assert.deepEqual(data.errors, []);
});
test("revisits an archived node and preserves independent new ideas", () => {
  const es = [design(1, [plan("c1")]), render(1, "c1"),
    { ...design(2, [plan("c2")]), turn: 1 }, render(2, "c2"),
    { ...design(3, [plan("c3", [ref(1, "c1")])]), turn: 2 }, render(3, "c3")];
  const data = GoalTree.collect(loop, es);
  assert.equal(data.nodes[1].parents.length, 0);
  assert.deepEqual(data.edges.map(e => `${e.from}->${e.to}`), ["1/c1/A->3/c3/A"]);
});
test("broken, failed, future, and duplicate identities surface without substituted parents", () => {
  const es = [design(1, [plan("c1")]), render(1, "c1", "A", false), render(1, "c1", "B"),
    { ...design(2, [plan("c3", [ref(1, "c1"), ref(1, "c2"), ref(2, "c3")])]), turn: 1 }, render(2, "c3"), render(2, "c3")];
  const data = GoalTree.collect(loop, es);
  assert.equal(data.errors.length, 4);
  assert.deepEqual(data.edges, []);
});
test("old loops have no invented lineage", () => {
  assert.deepEqual(GoalTree.collect({ protocolVersion: 7 }, [render(1, "c1")]), { nodes: [], edges: [], errors: [] });
});
