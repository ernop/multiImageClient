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
    let posts = 0, preparations = 0, seriesPreparations = 0, fail = false;
    const seenSeriesIndices = [];
    await page.route('https://share.test/**', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.endsWith('/prepare-series')) {
        seriesPreparations++;
        const postBody = route.request().postData() || '';
        const match = postBody.match(/name="imageIndices"\r?\n\r?\n([^\r\n]+)/);
        const indices = match ? match[1] : '0,0';
        seenSeriesIndices.push(indices);
        return route.fulfill({ json: {
          token: 'b'.repeat(64),
          kind: 'series',
          groupId: 'grp1',
          generator: 'gpt2',
          generatorLabel: 'GPT Image 2',
          pageCount: 2,
          serverName: 'Test server',
          channelName: 'vibecoders',
          threadName: 'Daily Thursday, September 17, 2026 image thread',
          publicUrl: 'https://share.test/shared/original/' + 'b'.repeat(64) + '/',
          viewUrl: 'https://share.test/shared/original/' + 'b'.repeat(64) + '/',
          pagePublished: false,
          previewUrl: 'preview',
          linkLabel: 'Read 1–2',
          reuseLabel: 'Make your own',
          totalBytes: 2400000,
          inputCount: 0,
          disclosure: 'Anyone with this link can see the source prompt, inputs, all 2 pages from GPT Image 2, their prompts, and the series contact sheet.',
          batches: [{ startPage: 1, endPage: 2, count: 2, totalBytes: 2400000 }],
          pages: [
            { index: 1, title: 'Opening', jobId: 'j1', imageIndex: 0, bytes: 1200000, previewUrl: 'media.svg' },
            { index: 2, title: 'Climax', jobId: 'j2', imageIndex: Number(indices.split(',')[1] || 0), bytes: 1200000, previewUrl: 'media.svg' },
          ],
        } });
      }
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
        const bodyText = route.request().postData() || '';
        assert.ok(bodyText.includes('name="confirmed"'));
        const isSeries = bodyText.includes('b'.repeat(64));
        return route.fulfill({ status: fail ? 502 : 200, json: fail
          ? { state: 'pending', error: 'The page is public. Check Discord.' }
          : { state: 'sent', item: isSeries
            ? { kind: 'series', groupId: 'grp1', generator: 'gpt2' }
            : { jobId: 'fixture', generator: 'gpt2', imageIndex: 0 } } });
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

    // Multi-page series preview: multi-variant picker re-prepares with selected indices and confirms.
    fail = false;
    await page.evaluate(() => {
      window.outcome = 'waiting';
      previewPublicSeriesShare({
        apiUrl: value => new URL(value, location.href).href,
        groupId: 'grp1',
        generator: 'gpt2',
        pageVariants: [
          { index: 1, title: 'Opening', jobId: 'j1', thumbs: ['media.svg'] },
          { index: 2, title: 'Climax', jobId: 'j2', thumbs: ['media.svg', 'media.svg'] },
        ],
      }).then(value => { window.outcome = value || 'cancelled'; }).catch(error => { window.outcome = error.state; });
    });
    await page.waitForFunction(() => !document.querySelector('.share-confirm').disabled);
    assert.equal(seriesPreparations, 1);
    assert.equal(seenSeriesIndices[0], '0,0');
    assert.equal(await page.locator('.share-series-picker').isVisible(), true);
    assert.match(await page.locator('.share-batches').textContent(), /1 Discord message/);
    assert.equal(await page.locator('.share-series-thumb').count(), 2);
    // Switch page 2 to variant #2 and verify re-prepare sends 0,1.
    await page.locator('.share-series-choice').nth(1).click();
    await page.waitForFunction(() => !document.querySelector('.share-confirm').disabled);
    assert.equal(seriesPreparations, 2);
    assert.equal(seenSeriesIndices[1], '0,1');
    await page.getByRole('button', { name: 'Make public & send' }).click();
    await page.waitForFunction(() => outcome.state === 'sent');
    assert.equal(posts, 3);

    assert.deepEqual(errors, []);
    console.log('PASS: preview, concise caption, disclosure, public-page preview, cancel, exact confirmation, uncertain-delivery blocking, and multi-page series picker.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
