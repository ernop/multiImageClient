const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../MultiImageClient/Ui/wwwroot');
const base = 'http://127.0.0.1:5960/';
const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="600"><rect width="800" height="600" fill="green"/></svg>';

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    // Read local configuration only. Every mutation stays inside this test.
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      if (url.origin !== new URL(base).origin) return route.abort();
      if (request.method() !== 'GET') return route.fulfill({ status: 400, json: { error: 'Test submission captured' } });
      const relative = url.pathname.slice(1) || 'index.html';
      const file = path.resolve(root, relative);
      if (file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) {
        return route.fulfill({ path: file });
      }
      return route.continue();
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base);
    await page.waitForFunction(() => generatorPreferences !== null);
    await page.locator('#global-append-text').fill('General directives 🂡\nKeep clear spacing.');
    await page.reload();
    await page.waitForFunction(() => generatorPreferences !== null);
    assert.equal(await page.locator('#global-append-text').inputValue(), 'General directives 🂡\nKeep clear spacing.');
    await page.evaluate(() => {
      const config = buildPersonalConfiguration();
      const normalized = normalizeImportedPersonalConfiguration(config);
      if (normalized.promptTools.globalAppendText !== globalAppendText.value) throw Error('round trip');
      const old = structuredClone(config);
      old.version = 2;
      delete old.promptTools.globalAppendText;
      if (normalizeImportedPersonalConfiguration(old).promptTools.globalAppendText !== '') throw Error('migration');
      for (const bad of [42, 'x'.repeat(16001)]) {
        const invalid = structuredClone(config);
        invalid.promptTools.globalAppendText = bad;
        let rejected = false;
        try { normalizeImportedPersonalConfiguration(invalid); } catch { rejected = true; }
        if (!rejected) throw Error('invalid append accepted');
      }
      const image = generators.find(g => g.kind !== 'describe' && g.available && !g.requiresImage && allGeneratorInputs().some(input => input.value === g.key));
      setEndpointFieldOverride(generatorPreferences, image.key, 'extraText', 'Endpoint directive');
      if (combinedEndpointExtraText(image.key) !== 'Endpoint directive\n\n' + globalAppendText.value) throw Error('suffix order');
      const describe = generators.find(g => g.kind === 'describe');
      if (describe && !combinedEndpointExtraText(describe.key).endsWith(globalAppendText.value)) throw Error('describe suffix');
      for (const input of allGeneratorInputs()) input.checked = input.value === image.key;
      promptBox.value = 'Test prompt';
      usernameInput.value = 'Fixture';
    });
    let submitted;
    await page.route('**/api/jobs', async route => {
      if (route.request().method() === 'GET') return route.continue();
      submitted = route.request().postDataBuffer().toString('utf8');
      return route.fulfill({ status: 400, json: { error: 'Test submission captured' } });
    });
    await page.evaluate(() => submit());
    assert.ok(submitted?.includes('Endpoint directive\\n\\nGeneral directives'), 'submitted suffix contains endpoint and global directives: ' + await page.locator('#send-error').textContent());

    const pending = [];
    await page.route('**/fixture-original/*', route => { pending.push(route); });
    await page.evaluate(() => {
      imageViewer.hidden = false;
      imageViewerCompareInput = false;
      window.fixtureItems = Array.from({ length: 25 }, (_, i) => ({ jobId: 'fixture', generator: 'test', imageIndex: i, url: location.origin + '/fixture-original/' + i }));
      window.fixturePrompts = [{ jobId: 'fixture', hasInput: false, items: fixtureItems }];
      window.plan = i => prepareImageViewerWindow(fixturePrompts, { promptIndex: 0, prompt: fixturePrompts[0], item: fixtureItems[i] });
      plan(10).promise.catch(() => {});
    });
    await page.waitForFunction(() => imageViewerPreloadActive === 6);
    await page.waitForTimeout(150);
    assert.equal(pending.length, 6, 'neighbors fetch before selected original completes');
    await page.evaluate(() => {
      const existing = imageViewerCache.get(fixtureItems[11].url);
      plan(11).promise.catch(() => {});
      if (imageViewerCache.get(fixtureItems[11].url) !== existing) throw Error('selected neighbor restarted');
    });
    for (let round = 0; round < 8; round++) {
      const batch = pending.splice(0);
      await Promise.all(batch.map(route => route.fulfill({ contentType: 'image/svg+xml', body: svg }).catch(() => {})));
      await page.waitForTimeout(100);
    }
    assert.equal(await page.evaluate(() => fixtureItems.slice(1, 22).every(item => imageViewerCache.get(item.url)?.ready)), true);
    await page.evaluate(() => { imageViewer.hidden = true; abortImageViewerInFlight(); });

    const viewerPage = await context.newPage();
    viewerPage.on('pageerror', error => errors.push(error.message));
    await viewerPage.goto(base);
    await viewerPage.setContent('<html><body></body></html>');
    await viewerPage.addScriptTag({ path: path.join(root, 'viewer.js') });
    const requests = [], thumbRequests = [];
    await viewerPage.route('**/fixture-viewer/**', route => {
      if (route.request().url().includes('/thumb/')) {
        thumbRequests.push(route.request().url());
        return route.fulfill({ contentType: 'image/svg+xml', body: svg });
      }
      requests.push(route);
    });
    await viewerPage.evaluate(() => {
      window.fixtureViewer = MultiImageViewer.create({ items: () => Array.from({ length: 31 }, (_, i) => ({
        id: String(i), url: '/fixture-viewer/original/' + i, thumbUrl: '/fixture-viewer/thumb/' + i, title: 'Image ' + i,
      })) });
      fixtureViewer.open('15');
    });
    await viewerPage.waitForTimeout(250);
    assert.equal(thumbRequests.length, 21, 'all range thumbnails requested');
    assert.equal(requests.length, 6, 'shared viewer starts neighbors before selection completes');
    await viewerPage.evaluate(() => fixtureViewer.open('16'));
    for (let round = 0; round < 8; round++) {
      const batch = requests.splice(0);
      await Promise.all(batch.map(route => route.fulfill({ contentType: 'image/svg+xml', body: svg }).catch(() => {})));
      await viewerPage.waitForTimeout(100);
    }
    assert.equal(await viewerPage.locator('.miv-title').textContent(), 'Image 16');
    assert.equal(await viewerPage.locator('.miv-badge').evaluate(el => el.hidden), true);
    await viewerPage.evaluate(() => { fixtureViewer.close(); fixtureViewer.open('0'); fixtureViewer.close(); fixtureViewer.open('30'); });
    for (let round = 0; round < 6; round++) {
      await Promise.all(requests.splice(0).map(route => route.fulfill({ contentType: 'image/svg+xml', body: svg }).catch(() => {})));
      await viewerPage.waitForTimeout(100);
    }
    assert.equal(await viewerPage.locator('.miv-title').textContent(), 'Image 30');
    assert.equal(await viewerPage.locator('.miv-badge').evaluate(el => el.hidden), true);
    assert.deepEqual(errors, []);
    console.log('PASS: global suffix persistence, migration, validation, submission; both viewers preload originals; navigation and reopen preserve identity.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
