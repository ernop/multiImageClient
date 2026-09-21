const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../MultiImageClient/Ui/wwwroot');
const dialog = fs.readFileSync(path.join(root, 'index.html'), 'utf8').match(/<dialog id="public-share-dialog"[\s\S]*?<\/dialog>/)[0];
const illustration = '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="450"><rect width="800" height="450" fill="#b9ddff"/><circle cx="620" cy="100" r="48" fill="#ffdd7c"/><path d="M0 390L220 130L470 390L650 230L800 390V450H0" fill="#6ca887"/><text x="24" y="420" font-family="sans-serif" font-size="24" fill="white">Preview fixture · no Discord message sent</text></svg>';

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 950 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    let posts = 0, preparations = 0, fail = false;
    await page.route('https://share.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('/prepare')) {
        preparations++;
        return route.fulfill({ json: { token: 'a'.repeat(64), jobId: 'fixture', generator: 'gpt2', imageIndex: 0,
          serverName: 'Test server', channelName: 'vibecoders', threadName: 'Daily Thursday, September 17, 2026 image thread', publicUrl: 'https://share.test/shared/original/' + 'a'.repeat(64) + '/',
          viewUrl: 'https://share.test/shared/original/' + 'a'.repeat(64) + '/#output-2',
          pagePublished: posts > 0,
          mediaKind: 'image', mediaUrl: 'media.svg', previewUrl: 'preview', inputCount: 1, outputCount: 3, hasContactSheet: true,
          linkLabel: 'View prompt', reuseLabel: 'Make your own',
          disclosure: 'Anyone with this link can see the prompt, inputs, all outputs, and contact sheet.' } });
      }
      if (url.pathname === '/api/discord/vibecoders') {
        posts++;
        assert.equal(route.request().headers()['x-mic-share'], '1');
        assert.ok(route.request().postData().includes('name="confirmed"'));
        return route.fulfill({ status: fail ? 502 : 200, json: fail
          ? { state: 'pending', error: 'The page is public. Check Discord.' }
          : { state: 'sent', item: { jobId: 'fixture', generator: 'gpt2', imageIndex: 0 } } });
      }
      if (url.pathname === '/media.svg') return route.fulfill({ contentType: 'image/svg+xml', body: illustration });
      if (url.pathname === '/preview') return route.fulfill({ contentType: 'text/html', body: '<h1>Shared prompt</h1><p>Fixture prompt, inputs, outputs, and contact sheet.</p>' });
      return route.fulfill({ contentType: 'text/html', body: '<html><head><style>' + fs.readFileSync(path.join(root, 'style.css'), 'utf8') + '</style></head><body>' + dialog + '</body></html>' });
    });
    await page.goto('https://share.test/');
    await page.addScriptTag({ path: path.join(root, 'public-share-dialog.js') });
    const open = () => page.evaluate(() => {
      window.outcome = 'waiting';
      previewPublicShare({ apiUrl: value => new URL(value, location.href).href, jobId: 'fixture', generator: 'gpt2', imageIndex: 0 })
        .then(value => { window.outcome = value || 'cancelled'; }).catch(error => { window.outcome = error.state; });
    });
    await open();
    await page.waitForFunction(() => !document.querySelector('.share-confirm').disabled);
    assert.equal(posts, 0);
    assert.equal(await page.locator('.share-destination').textContent(), 'Post to Test server · #vibecoders → Daily Thursday, September 17, 2026 image thread (Pacific time)');
    assert.equal(await page.locator('.share-caption').textContent(), 'View prompt · Make your own');
    assert.ok((await page.locator('.share-caption a').first().getAttribute('href')).endsWith('/#output-2'));
    assert.ok((await page.locator('.share-caption a').last().getAttribute('href')).endsWith('/reuse'));
    assert.ok((await page.locator('.share-disclosure').textContent()).includes('Anyone with this link'));
    const screenshot = path.resolve(__dirname, '../.local-ui/public-share-preview.png');
    fs.mkdirSync(path.dirname(screenshot), { recursive: true });
    await page.screenshot({ path: screenshot });
    await page.getByRole('link', { name: 'View prompt' }).click();
    assert.equal(await page.locator('details').evaluate(el => el.open), true);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await page.evaluate(() => outcome), 'cancelled');
    assert.equal(posts, 0);
    await open();
    await page.waitForFunction(() => !document.querySelector('.share-confirm').disabled);
    await page.getByRole('button', { name: 'Make public & send' }).click();
    await page.waitForFunction(() => outcome.state === 'sent');
    assert.equal(posts, 1);
    fail = true;
    await open();
    await page.waitForFunction(() => !document.querySelector('.share-confirm').disabled);
    assert.ok((await page.locator('.share-status').textContent()).includes('already has a public page'));
    await page.getByRole('button', { name: 'Send to thread' }).click();
    await page.getByRole('button', { name: 'Close', exact: true }).waitFor();
    assert.equal(await page.locator('.share-confirm').isDisabled(), true);
    await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => outcome), 'pending');
    assert.equal(posts, 2);
    assert.equal(preparations, 3);
    assert.deepEqual(errors, []);
    console.log('PASS: preview, concise caption, disclosure, public-page preview, cancel, exact confirmation, and uncertain-delivery blocking.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
