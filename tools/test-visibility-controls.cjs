const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const source = fs.readFileSync(process.env.MIC_VISIBILITY_TEST_SOURCE
  || require('node:path').join(__dirname, '../MultiImageClient/Ui/wwwroot/app.js'), 'utf8');
function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from);
  assert.ok(from >= 0 && to > from);
  return source.slice(from, to);
}
function harness() {
  const button = {};
  const connection = { classList: { remove() {} } };
  const card = { dataset: { jobId: 'job', state: 'running', canHide: 'true' },
    querySelectorAll: selector => selector === '.hide-prompt' ? [button] : [],
    querySelector: () => connection };
  const viewer = {};
  const alerts = [];
  const context = vm.createContext({
    el: () => card,
    imageViewer: { hidden: false }, imageViewerHide: viewer,
    locateImageViewerState: () => ({ item: { jobId: 'job', kind: 'image' } }),
    getImageViewerPrompts: () => [],
    findViewerAnchor: () => ({ closest: () => card }),
    confirm: () => true, alert: message => alerts.push(message),
    favoritesGrid: { querySelectorAll: () => [] }, updateJobProgress() {},
    persistHiddenResource: async () => { throw new Error('The item is hidden, but permanent file deletion is incomplete.'); },
  });
  vm.runInContext('let visibilityMutation = null;\n'
    + section('function deletionWaitReason(', 'function createHidePromptButton(')
    + section('function renderImageViewerHide(', 'function vibecodersIdentity(')
    + section('async function hideCurrentViewerImage(', 'async function toggleImageViewerFavorite(')
    + section('function applyJobEvent(', 'setInterval(() => {'), context);
  return { context, card, button, viewer, alerts };
}
test('running jobs block prompt and image deletion, then job completion enables the open viewer', async () => {
  const h = harness();
  h.context.refreshJobDeletionControls(h.card);
  assert.equal(h.button.disabled, true);
  assert.equal(h.viewer.disabled, true);
  assert.match(h.viewer.textContent, /when job finishes/);
  await h.context.hideCurrentViewerImage();
  assert.equal(h.alerts.length, 0);
  h.context.applyJobEvent('job', h.card, { type: 'job-done' });
  assert.equal(h.button.disabled, false);
  assert.equal(h.viewer.disabled, false);
  assert.equal(h.viewer.title, '');
  await h.context.hideCurrentViewerImage();
  assert.match(h.alerts[0], /permanent file deletion is incomplete/);
});
test('favorites use their matching live job state and preserve server checks without a loaded job', () => {
  const h = harness();
  const favorite = { dataset: { jobId: 'job' } };
  assert.match(h.context.deletionWaitReason(favorite), /all generators/);
  h.card.dataset.state = 'done';
  assert.equal(h.context.deletionWaitReason(favorite), '');
  h.context.el = () => null;
  assert.equal(h.context.deletionWaitReason(favorite), '');
});
test('queued jobs remain blocked and missing creator permission remains hidden', () => {
  const h = harness();
  h.card.dataset.state = 'queued';
  h.context.refreshJobDeletionControls(h.card);
  assert.equal(h.button.disabled, true);
  h.card.dataset.state = 'done';
  h.card.dataset.canHide = 'false';
  h.context.refreshJobDeletionControls(h.card);
  assert.equal(h.viewer.hidden, true);
  assert.equal(h.viewer.disabled, true);
});
