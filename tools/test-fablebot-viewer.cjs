const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(require('node:path').join(__dirname, '../MultiImageClient/Ui/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function renderImageViewerFableBot(item)');
const end = source.indexOf('async function hideCurrentViewerImage()', start);
assert.ok(start > 0 && end > start);

function harness() {
  const requests = [];
  const button = { addEventListener(type, handler) { this.click = handler; } };
  const context = vm.createContext({
    imageViewerFableBot: button, URLSearchParams, FormData,
    apiUrl: value => value,
    fetch: (url, options) => new Promise(resolve => requests.push({ url, options, resolve })),
    getImageViewerPrompts: () => [],
    locateImageViewerState: () => context.current,
  });
  vm.runInContext('let fableBotAvailable = true; let fableBotSelection = ""; let fableBotRenderVersion = 0; let fableBotSending = false;\n'
    + source.slice(start, end), context);
  return { context, button, requests, render: item => context.renderImageViewerFableBot(item) };
}
const item = imageIndex => ({ jobId: 'job', generator: 'gpt2', imageIndex, kind: 'image' });
const answer = (request, body, ok = true) => request.resolve({ ok, json: async () => body });
const settle = () => new Promise(resolve => setImmediate(resolve));

test('late status cannot overwrite a later selection or reopened image', async () => {
  const h = harness();
  h.render(item(0));
  h.render(item(1));
  h.render(item(0));
  answer(h.requests[2], { state: 'sent' });
  await settle();
  answer(h.requests[0], { state: 'ready' });
  answer(h.requests[1], { state: 'ready' });
  await settle();
  assert.equal(h.button.textContent, 'sent to Discord');
  assert.equal(h.button.disabled, true);
  h.render(null);
  assert.equal(h.button.hidden, true);
  assert.equal(h.button.title, '');
});

test('send captures the selected identity and blocks an uncertain duplicate', async () => {
  const h = harness();
  h.context.current = { item: item(3) };
  h.render(item(3));
  answer(h.requests[0], { state: 'ready' });
  await settle();
  const pending = h.button.click();
  assert.equal(h.button.disabled, true);
  const post = h.requests[1];
  assert.equal(post.options.body.get('imageIndex'), '3');
  assert.equal(post.options.body.get('generator'), 'gpt2');
  assert.equal(post.options.headers['X-MIC-FableBot'], '1');
  answer(post, { state: 'pending', error: 'Check Discord.' }, false);
  await pending;
  assert.equal(h.button.textContent, 'check delivery in Discord');
  await h.button.click();
  assert.equal(h.requests.length, 2);
});
