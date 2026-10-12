const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../MultiImageClient/Ui/wwwroot/app.js'), 'utf8');

function slice(startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start > 0 && end > start, `${startMarker} not found`);
  return source.slice(start, end);
}

function button() {
  const classes = new Set();
  return {
    disabled: false, textContent: '', title: '',
    classList: {
      add: (...names) => names.forEach(name => classes.add(name)),
      remove: (...names) => names.forEach(name => classes.delete(name)),
      contains: name => classes.has(name),
    },
  };
}

const abortError = () => Object.assign(new Error('discarded'), { name: 'AbortError' });
const viewedUrl = '/api/jobs/job/images/gpt2/0';

function activationHarness({ entryPromise, fetchResponse }) {
  const setImage = button();
  const setImagePrompt = button();
  const setPrompt = button();
  const fetches = [];
  const attached = [];
  const prompts = [];
  const context = vm.createContext({
    AbortController,
    imageViewerSetImage: setImage,
    imageViewerSetImagePrompt: setImagePrompt,
    imageViewerActivationButtons: () => [setImage, setImagePrompt, setPrompt],
    getImageViewerPrompts: () => [],
    locateImageViewerState: () => ({
      item: { jobId: 'job', generator: 'gpt2', imageIndex: 0, url: viewedUrl },
      prompt: { prompt: 'viewed prompt' },
    }),
    apiUrl: value => value,
    loadImageViewerEntry: () => ({ promise: entryPromise(), blobUrl: 'blob:decoded' }),
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return fetchResponse(url);
    },
    setImagesFromBlobs: async blobs => { attached.push(blobs); },
    applyViewedPromptToComposer: prompt => { prompts.push(prompt); },
    sendError: { textContent: 'stale error' },
    imageViewerIdentityMatches: () => true,
    renderImageViewerActiveActions: () => {},
    setTimeout: () => 0,
  });
  vm.runInContext('let imageViewerActivationVersion = 0; let imageViewerActivationController = null;\n'
    + slice('async function setViewedImageActive(includePrompt)', 'function setViewedPromptActive()'), context);
  return { context, setImage, setImagePrompt, fetches, attached, prompts };
}

const imageBlob = { type: 'image/png' };
const imageResponse = { ok: true, status: 200, blob: async () => imageBlob };

test('set image active reuses the decoded viewer entry', async () => {
  const h = activationHarness({ entryPromise: async () => {}, fetchResponse: () => imageResponse });
  await h.context.setViewedImageActive(false);
  assert.deepEqual(h.fetches.map(call => call.url), ['blob:decoded']);
  assert.equal(h.attached.length, 1);
  assert.equal(h.attached[0][0], imageBlob);
  assert.deepEqual(h.prompts, []);
  assert.equal(h.setImage.textContent, 'image active');
});

test('set image active downloads directly once the shared entry was discarded', async () => {
  const h = activationHarness({
    entryPromise: () => Promise.reject(abortError()),
    fetchResponse: () => imageResponse,
  });
  await h.context.setViewedImageActive(true);
  assert.deepEqual(h.fetches.map(call => call.url), [viewedUrl]);
  assert.equal(h.fetches[0].options.cache, 'force-cache');
  assert.equal(h.attached.length, 1);
  assert.equal(h.attached[0][0], imageBlob);
  assert.deepEqual(h.prompts, ['viewed prompt']);
  assert.equal(h.setImagePrompt.textContent, 'image + prompt active');
  assert.equal(h.setImagePrompt.classList.contains('error'), false);
});

test('a direct download that is not an image attaches nothing', async () => {
  const h = activationHarness({
    entryPromise: () => Promise.reject(abortError()),
    fetchResponse: () => ({ ok: true, status: 200, blob: async () => ({ type: 'text/html' }) }),
  });
  await h.context.setViewedImageActive(false);
  assert.equal(h.attached.length, 0);
  assert.equal(h.setImage.textContent, 'set active failed');
  assert.match(h.setImage.title, /text\/html/);
});
