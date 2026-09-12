const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../MultiImageClient/Ui/wwwroot');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const errors = [];
    const assets = new Map(['index.html', 'goal.html', 'app.js', 'goal.js', 'text-drafts.js']
      .map(name => [name, path.join(root, name)]));
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.origin !== new URL(base).origin) return route.continue();
      if (route.request().method() !== 'GET') return route.abort(); // Never create real jobs.
      const name = url.pathname.split('/').pop() || 'index.html';
      if (assets.has(name)) return route.fulfill({ path: assets.get(name) });
      return route.continue();
    });
    const pages = await Promise.all(Array.from({ length: 4 }, () => context.newPage()));
    for (const page of pages) page.on('pageerror', error => errors.push(error.message));
    const prompts = pages.map((_, i) => `Tab ${i + 1}: café 🂡\n  Exact spacing, punctuation, and unfinished text…`);
    for (let i = 0; i < pages.length; i++) {
      const page = pages[i];
      await page.goto(base);
      await page.locator('#prompt').fill(prompts[i]);
      await page.locator('#prompt').evaluate(field => {
        field.setSelectionRange(7, 12, 'backward');
        field.dispatchEvent(new Event('select'));
      });
    }
    for (let i = 0; i < pages.length; i++) {
      await pages[i].reload();
      assert.equal(await pages[i].locator('#prompt').inputValue(), prompts[i]);
      assert.deepEqual(await pages[i].locator('#prompt').evaluate(field =>
        [field.selectionStart, field.selectionEnd, field.selectionDirection]), [7, 12, 'backward']);
    }
    const page = pages[0];
    await page.evaluate(() => applyViewedPromptToComposer('A prompt activated from history.'));
    assert.ok(await page.evaluate(() => Object.keys(sessionStorage)
      .some(key => key.includes('mic_text_draft_v1:') && sessionStorage.getItem(key).includes('activated from history'))));
    await page.reload();
    assert.equal(await page.locator('#prompt').inputValue(), 'A prompt activated from history.');
    await page.goto(new URL('goal.html', base).href);
    await page.locator('#goal-text').fill('My unfinished goal 🂡\nwith a second line');
    await page.reload();
    assert.equal(await page.locator('#goal-text').inputValue(), 'My unfinished goal 🂡\nwith a second line');
    await page.waitForFunction(() => typeof config !== 'undefined' && config && selectedGeneratorKeys().length > 0);
    if (await page.locator('#goal-user').isVisible()) await page.locator('#goal-user').fill('Draft test');
    let accept, requested;
    const acceptance = new Promise(resolve => { accept = resolve; });
    const requestStarted = new Promise(resolve => { requested = resolve; });
    await page.route('**/api/goal-loops', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      requested();
      await acceptance;
      return route.fulfill({ json: { id: 'draft-test' } });
    });
    await page.locator('#goal-start').click();
    await requestStarted;
    await page.locator('#goal-text').fill('A newer goal typed while submission is pending.');
    accept();
    await page.waitForFunction(() => !document.getElementById('goal-start').disabled);
    assert.equal(await page.locator('#goal-text').inputValue(), 'A newer goal typed while submission is pending.');
    await page.reload();
    assert.equal(await page.locator('#goal-text').inputValue(), 'A newer goal typed while submission is pending.');
    await page.goto(base);
    assert.equal(await page.locator('#prompt').inputValue(), 'A prompt activated from history.');
    await page.locator('#prompt').fill('');
    await page.reload();
    assert.equal(await page.locator('#prompt').inputValue(), '');
    assert.equal(await pages[1].locator('#prompt').inputValue(), prompts[1]);

    // The original environment uses legacy storage; draft keys must still isolate accounts.
    let account = 'alice';
    await page.route('**/environment.js', route => route.fulfill({ contentType: 'text/javascript',
      body: 'window.MicEnvironment=' + JSON.stringify({ id: 'original', user: account, name: 'Draft test', role: 'admin' }) }));
    await page.reload();
    await page.locator('#prompt').fill('Alice owns this unfinished text.');
    account = 'bob';
    await page.reload();
    assert.equal(await page.locator('#prompt').inputValue(), '');
    await page.locator('#prompt').fill('Bob has a different draft.');
    account = 'alice';
    await page.reload();
    assert.equal(await page.locator('#prompt').inputValue(), 'Alice owns this unfinished text.');

    const denied = await browser.newContext();
    await denied.addInitScript(() => Object.defineProperty(window, 'sessionStorage', {
      get() { throw new DOMException('Storage disabled', 'SecurityError'); },
    }));
    const blocked = await denied.newPage();
    await blocked.route('**/*', route => {
      const name = new URL(route.request().url()).pathname.split('/').pop() || 'index.html';
      if (assets.has(name)) return route.fulfill({ path: assets.get(name) });
      return route.continue();
    });
    await blocked.goto(base);
    await blocked.locator('#prompt').fill('Still editable when storage is blocked.');
    assert.equal(await blocked.locator('#prompt').inputValue(), 'Still editable when storage is blocked.');
    assert.ok(await blocked.getByRole('status').filter({ hasText: 'Draft saving is unavailable' }).isVisible());
    assert.deepEqual(errors, []);
    console.log('PASS: four tabs retain distinct text and selection; goal/composer and accounts stay separate; late submissions preserve edits; activation, clearing, and blocked storage work.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
