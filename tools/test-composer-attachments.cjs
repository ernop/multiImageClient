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

function fakeNode() {
  return {
    className: '', hidden: false, children: [],
    set textContent(value) { this.children = [String(value)]; },
    get textContent() {
      return this.children.map(child => typeof child === 'string' ? child : child.textContent).join('');
    },
    append(...nodes) { this.children.push(...nodes); },
    appendChild(node) { this.children.push(node); return node; },
    replaceChildren(...nodes) { this.children = nodes; },
  };
}

const routingCatalog = [
  { key: 'gpt25-sunburst', label: 'GPT Image 2.5 Sunburst', inputImageLimit: 4 },
  { key: 'grok-api-pro', label: 'grok-api 2.0', inputImageLimit: 5 },
  { key: 'google', label: 'Nano Banana 2', inputImageLimit: 1 },
  { key: 'gpt1', label: 'gpt-image-1', inputImageLimit: 0 },
  { key: 'describe-claude', label: 'Claude describe', inputImageLimit: 5, kind: 'describe' },
];

function routingHarness(imageCount, checkedKeys) {
  const hint = fakeNode();
  hint.textContent = 'stale note';
  const context = vm.createContext({
    el: id => { assert.equal(id, 'multi-input-hint'); return hint; },
    document: { createElement: () => fakeNode() },
    inputImageItems: Array.from({ length: imageCount }, () => ({})),
    generators: routingCatalog,
    genLabel: key => routingCatalog.find(entry => entry.key === key).label,
    allGeneratorInputs: () => routingCatalog.map(entry => ({
      value: entry.key,
      checked: checkedKeys.includes(entry.key),
      dataset: { kind: entry.kind || 'image' },
    })),
  });
  vm.runInContext(slice('function generatorInputImageLimit(key)', '// ---------- prompt length limits'), context);
  context.renderMultiInputHint();
  return { context, hint, lines: hint.children.map(child => typeof child === 'string' ? child : child.textContent) };
}

test('the routing note lists each selected generator under the images it receives', () => {
  const { hint, lines } = routingHarness(5, routingCatalog.map(entry => entry.key));
  assert.equal(hint.hidden, false);
  assert.deepEqual(lines, [
    'All 5 images: grok-api 2.0',
    'Images 1–4: GPT Image 2.5 Sunburst',
    'Image 1 only: Nano Banana 2',
    'No image (text only): gpt-image-1',
    'One call per image: Claude describe',
    'Images arrive in the order shown. Say in the prompt how to use each one.',
  ]);
});

test('a share at or above the attachment count reads as all images', () => {
  const { lines } = routingHarness(2, ['gpt25-sunburst', 'grok-api-pro', 'google']);
  assert.deepEqual(lines, [
    'All 2 images: GPT Image 2.5 Sunburst, grok-api 2.0',
    'Image 1 only: Nano Banana 2',
    'Images arrive in the order shown. Say in the prompt how to use each one.',
  ]);
});

test('the routing note omits ordering advice when no generator receives two images', () => {
  const { lines } = routingHarness(3, ['google', 'gpt1']);
  assert.deepEqual(lines, ['Image 1 only: Nano Banana 2', 'No image (text only): gpt-image-1']);
});

test('the routing note hides below two attachments and reports an empty selection', () => {
  const single = routingHarness(1, ['google']);
  assert.equal(single.hint.hidden, true);
  assert.deepEqual(single.lines, []);
  const none = routingHarness(2, []);
  assert.equal(none.hint.hidden, false);
  assert.deepEqual(none.lines, ['No generator selected.']);
});

const clipImage = { type: 'image/png', name: 'clip.png' };

function clipboard({ text = '', html = '', rtf = false }) {
  const types = [];
  if (text) types.push('text/plain');
  if (html) types.push('text/html');
  if (rtf) types.push('text/rtf');
  types.push('Files');
  const data = { 'text/plain': text, 'text/html': html, 'text/rtf': rtf ? '{\\rtf1 copied}' : '' };
  return { types, files: [clipImage], items: [], getData: type => data[type] || '' };
}

const officeCopy = () => clipboard({
  text: 'Quarterly totals',
  html: '<html><body><!--StartFragment--><p>Quarterly totals</p><!--EndFragment--></body></html>',
  rtf: true,
});
const textarea = { tagName: 'TEXTAREA' };
const body = { tagName: 'BODY' };

function pasteHarness({ viewerOpen = false, sketchOpen = false, openDialog = null } = {}) {
  let listener = null;
  const appended = [];
  const sketched = [];
  const context = vm.createContext({
    document: {
      addEventListener: (type, handler) => { assert.equal(type, 'paste'); listener = handler; },
      querySelector: selector => { assert.equal(selector, 'dialog[open]'); return openDialog; },
    },
    DOMParser: class {
      parseFromString(html) {
        const bodyText = (/<body[^>]*>([\s\S]*)<\/body>/i.exec(html)?.[1] ?? html)
          .replace(/<!--[\s\S]*?-->/g, '').replace(/<[^>]*>/g, '');
        return { body: { textContent: bodyText } };
      }
    },
    imageViewer: { hidden: !viewerOpen },
    sketchDialog: { open: sketchOpen },
    appendImage: file => { appended.push(file); return true; },
    importImageIntoSketch: file => { sketched.push(file); },
  });
  vm.runInContext(slice('function clipboardImageFile(clipboardData)', 'function cloneSketchImageData'), context);
  vm.runInContext(slice('// Paste works anywhere on the page', 'pasteZone.addEventListener("click"'), context);
  const paste = (target, clipboardData) => {
    const event = { target, clipboardData, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
    listener(event);
    return event;
  };
  return { paste, appended, sketched };
}

test('an office copy pasted into a text field pastes only its text', () => {
  const h = pasteHarness();
  for (const target of [textarea, { tagName: 'INPUT', type: 'text' }, { tagName: 'DIV', isContentEditable: true }]) {
    assert.equal(h.paste(target, officeCopy()).defaultPrevented, false);
  }
  const htmlOnly = clipboard({ text: 'Quarterly totals', html: '<p>Quarterly totals</p>' });
  assert.equal(h.paste(textarea, htmlOnly).defaultPrevented, false);
  assert.deepEqual(h.appended, []);
});

test('browser image copies pasted into the prompt still attach the image', () => {
  const h = pasteHarness();
  const chrome = clipboard({ html: '<meta charset="utf-8"><img src="https://example.com/a.png">' });
  const firefox = clipboard({ text: 'https://example.com/a.png', html: '<img src="https://example.com/a.png">' });
  const screenshot = clipboard({});
  for (const copy of [chrome, firefox, screenshot]) {
    assert.equal(h.paste(textarea, copy).defaultPrevented, true);
  }
  assert.deepEqual(h.appended, [clipImage, clipImage, clipImage]);
});

test('an office copy pasted outside a text field attaches its picture', () => {
  const h = pasteHarness();
  assert.equal(h.paste(body, officeCopy()).defaultPrevented, true);
  assert.equal(h.paste({ tagName: 'TEXTAREA', readOnly: true }, officeCopy()).defaultPrevented, true);
  assert.equal(h.paste({ tagName: 'INPUT', type: 'checkbox' }, officeCopy()).defaultPrevented, true);
  assert.deepEqual(h.appended, [clipImage, clipImage, clipImage]);
});

test('a paste behind the viewer or another dialog attaches nothing', () => {
  for (const state of [{ viewerOpen: true }, { openDialog: { id: 'video-dialog' } }]) {
    const h = pasteHarness(state);
    assert.equal(h.paste(body, clipboard({})).defaultPrevented, false);
    assert.deepEqual(h.appended, []);
  }
});

test('the open sketch editor takes a pasted image but leaves legend text alone', () => {
  const h = pasteHarness({ sketchOpen: true, openDialog: { id: 'sketch-dialog' } });
  assert.equal(h.paste(body, clipboard({})).defaultPrevented, true);
  assert.equal(h.paste({ tagName: 'INPUT', type: 'text' }, officeCopy()).defaultPrevented, false);
  assert.deepEqual(h.sketched, [clipImage]);
  assert.deepEqual(h.appended, []);
});

test('chip tooltips state each generator share of the attachments', () => {
  const { context } = routingHarness(1, []);
  assert.equal(context.inputImageShareText(5, 3), 'will receive all 3 attached images, in order');
  assert.equal(context.inputImageShareText(4, 5), 'will receive only the first 4 of 5 attached images');
  assert.equal(context.inputImageShareText(1, 3), 'will receive only the first of 3 attached images');
});
