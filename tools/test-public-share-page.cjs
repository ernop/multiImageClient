const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

// Generate this fixture with PublicPageSelectionUsesExactAssetSlotsAndKeepsReuseSeparate.
const fixture = process.env.MIC_PUBLIC_SHARE_HTML_FIXTURE;
if (!fixture) throw new Error('Set MIC_PUBLIC_SHARE_HTML_FIXTURE to the C# renderer fixture.');
const html = fs.readFileSync(fixture, 'utf8');
const publicUrl = 'https://share.test/shared/original/' + 'a'.repeat(64) + '/';
const illustration = '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="450"><rect width="800" height="450" fill="#b9ddff"/><circle cx="620" cy="100" r="48" fill="#ffdd7c"/><path d="M0 390L220 130L470 390L650 230L800 390V450H0" fill="#6ca887"/><text x="24" y="420" font-family="sans-serif" font-size="24" fill="white">Shared prompt fixture</text></svg>';

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of [{ width: 1100, height: 950 }, { width: 390, height: 844 }]) {
      const page = await browser.newPage({ viewport });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      let releaseImages;
      const imagesReady = new Promise(resolve => { releaseImages = resolve; });
      await page.route('https://share.test/**', async route => {
        const url = new URL(route.request().url());
        assert.equal(route.request().method(), 'GET');
        if (url.pathname.includes('/asset/')) {
          if (url.pathname.endsWith('/4')) return route.fulfill({ status: 204 });
          await imagesReady;
          return route.fulfill({ contentType: 'image/svg+xml', body: illustration });
        }
        assert.equal(url.href, publicUrl);
        return route.fulfill({ contentType: 'text/html', body: html });
      });
      await page.goto(publicUrl + '#output-3', { waitUntil: 'domcontentloaded' });
      const selected = page.locator('#output-3');
      await selected.locator('.selected-output').waitFor({ state: 'visible' });
      const initial = await selected.boundingBox();
      assert.ok(initial.y >= 0 && initial.y < 50, `Initial selected position: ${initial.y}`);
      releaseImages();
      await page.waitForFunction(() => [...document.querySelectorAll('#outputs img')].every(image => image.complete && image.naturalWidth > 0));
      const loaded = await selected.boundingBox();
      assert.ok(Math.abs(loaded.y - initial.y) < 2, 'Loading images moved the selected output.');
      assert.equal(await selected.evaluate(el => getComputedStyle(el).borderTopColor), 'rgb(53, 72, 200)');
      assert.equal(await page.locator('.selected-output:visible').count(), 1);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      const screenshots = path.resolve(__dirname, '../.local-ui');
      fs.mkdirSync(screenshots, { recursive: true });
      await page.screenshot({ path: path.join(screenshots, `public-share-output-${viewport.width}.png`) });
      await selected.getByRole('link', { name: 'View prompt', exact: true }).click();
      assert.equal(new URL(page.url()).hash, '#prompt');
      assert.equal(await page.locator('.selected-output:visible').count(), 0);
      assert.ok((await page.locator('#prompt').boundingBox()).y < 50);
      await page.goto(publicUrl + '#output-2', { waitUntil: 'domcontentloaded' });
      assert.equal(await page.locator('#output-2 .selected-output').isVisible(), true);
      assert.equal(await selected.locator('.selected-output').isVisible(), false);
      await page.locator('#output-2 img').click();
      assert.equal(page.url(), publicUrl + 'asset/2');
      assert.deepEqual(errors, []);
      await page.close();
    }

    const seriesFixture = process.env.MIC_PUBLIC_SERIES_SHARE_HTML_FIXTURE;
    if (seriesFixture) {
      const seriesHtml = fs.readFileSync(seriesFixture, 'utf8');
      const seriesUrl = 'https://share.test/shared/original/' + 'b'.repeat(64) + '/';
      for (const viewport of [{ width: 1100, height: 950 }, { width: 390, height: 844 }]) {
        const page = await browser.newPage({ viewport });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.route('https://share.test/**', async route => {
          const url = new URL(route.request().url());
          if (url.pathname.includes('/asset/')) {
            return route.fulfill({ contentType: 'image/svg+xml', body: illustration });
          }
          return route.fulfill({ contentType: 'text/html', body: seriesHtml });
        });
        await page.goto(seriesUrl, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => document.querySelector('#reader-counter')?.textContent?.trim() === '1 / 3');
        assert.equal(await page.locator('#reader-shell').isVisible(), true);
        assert.equal(await page.locator('#all-pages').isVisible(), false);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Series overflow at ${viewport.width}px`);
        // Next button advances to page 2 and syncs #page-2 hash.
        await page.locator('#reader-next').click();
        assert.equal(new URL(page.url()).hash, '#page-2');
        assert.equal(await page.locator('#reader-counter').textContent(), '2 / 3');
        // Keyboard ArrowRight advances to page 3, ArrowLeft steps back to page 2.
        await page.keyboard.press('ArrowRight');
        assert.equal(new URL(page.url()).hash, '#page-3');
        await page.keyboard.press('ArrowLeft');
        assert.equal(new URL(page.url()).hash, '#page-2');
        // Collapsible per-page prompt starts closed and opens on click.
        assert.equal(await page.locator('#reader-prompt-details').evaluate(el => el.open), false);
        await page.locator('#reader-prompt-summary').click();
        assert.equal(await page.locator('#reader-prompt-details').evaluate(el => el.open), true);
        assert.match(await page.locator('#reader-prompt-text').textContent(), /Page 2 prompt text/);
        // Toggle All pages vertical view and back.
        await page.locator('#mode-all').click();
        assert.equal(await page.locator('#all-pages').isVisible(), true);
        assert.equal(await page.locator('#reader-shell').isVisible(), false);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Series all-pages overflow at ${viewport.width}px`);
        await page.locator('#mode-page').click();
        assert.equal(await page.locator('#reader-shell').isVisible(), true);
        assert.equal(await page.locator('#all-pages').isVisible(), false);
        assert.deepEqual(errors, []);
        await page.close();
      }
      console.log('PASS: series public reader stepping, keyboard navigation, hash sync, all-pages toggle, and desktop/mobile layout.');
    }
    console.log('PASS: anonymous image selection, delayed images, prompt navigation, originals, and desktop/mobile layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
