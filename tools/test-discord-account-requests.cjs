const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const fixtures = process.env.MIC_DISCORD_SIGNUP_FIXTURES;
if (!fixtures) throw new Error('Set MIC_DISCORD_SIGNUP_FIXTURES to the C# fixture directory.');
const requestHtml = fs.readFileSync(path.join(fixtures, 'request.html'), 'utf8');
const claimHtml = fs.readFileSync(path.join(fixtures, 'claim.html'), 'utf8');
const publicHtml = fs.readFileSync(path.join(fixtures, 'public.html'), 'utf8');
const script = claimHtml.match(/<script>([\s\S]*?)<\/script>/)[1];
const scriptHash = crypto.createHash('sha256').update(script).digest('base64');
const signupUrl = 'https://share.test/shared/original/signup';
const publicUrl = 'https://share.test/shared/vibecoders-ai-generation/' + 'c'.repeat(64) + '/';
const token = 'a'.repeat(32) + '.' + 'b'.repeat(43);

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of [{ width: 1100, height: 850 }, { width: 390, height: 844 }]) {
      const page = await browser.newPage({ viewport });
      const errors = [], requests = [], redemptions = [];
      page.on('pageerror', error => errors.push(error.message));
      let reject = true;
      await page.route('https://share.test/**', async route => {
        const request = route.request(), url = request.url();
        assert.ok(!url.includes(token), 'A token entered an HTTP URL.');
        if (url === publicUrl) return route.fulfill({ contentType: 'text/html', body: publicHtml });
        if (url.startsWith(publicUrl + 'asset/')) return route.fulfill({ contentType: 'image/svg+xml',
          body: '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#ccd"/></svg>' });
        if (url === signupUrl + '/request') {
          assert.equal(request.method(), 'POST');
          const fields = new URLSearchParams(request.postData());
          requests.push(fields.get('username'));
          return route.fulfill({ contentType: 'text/html', body: '<p>Check your Discord DMs for the account link.</p>' });
        }
        if (url === signupUrl + '/claim' && request.method() === 'POST') {
          assert.equal(request.headers().origin, 'https://share.test');
          redemptions.push(new URLSearchParams(request.postData()).get('token'));
          return route.fulfill({ status: reject ? 400 : 200, json: reject
            ? { error: 'This account link is invalid, expired, or already used.' }
            : { destination: 'https://share.test/vibecoders-ai-generation/' } });
        }
        if (url === signupUrl + '/claim') return route.fulfill({ contentType: 'text/html', body: claimHtml,
          headers: { 'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; connect-src 'self'; script-src 'sha256-" + scriptHash + "'" } });
        if (url === signupUrl) return route.fulfill({ contentType: 'text/html', body: requestHtml });
        if (url === 'https://share.test/vibecoders-ai-generation/') return route.fulfill({ contentType: 'text/html', body: '<h1>Vibecoders</h1>' });
        throw new Error('Unexpected browser URL: ' + url);
      });
      await page.goto(publicUrl + '#output-0');
      await page.getByRole('link', { name: 'Request account', exact: true }).last().click();
      await page.waitForURL(signupUrl);
      await page.getByLabel('Discord username', { exact: true }).fill('alice.discord');
      const screenshots = path.resolve(__dirname, '../.local-ui');
      fs.mkdirSync(screenshots, { recursive: true });
      await page.screenshot({ path: path.join(screenshots, `discord-account-request-${viewport.width}.png`) });
      await page.getByRole('button', { name: 'Send me an account link' }).click();
      await page.getByText('Check your Discord DMs for the account link.').waitFor();
      assert.deepEqual(requests, ['alice.discord']);
      assert.equal(redemptions.length, 0);
      await page.goto(signupUrl + '/claim#' + token);
      await page.waitForFunction(() => !document.querySelector('button').disabled);
      assert.equal(page.url(), signupUrl + '/claim');
      assert.equal(redemptions.length, 0, 'Opening a link must not consume it.');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(screenshots, `discord-account-claim-${viewport.width}.png`) });
      await page.getByRole('button', { name: 'Continue to Vibecoders' }).click();
      await page.getByText('This account link is invalid, expired, or already used.').waitFor();
      assert.deepEqual(redemptions, [token]);
      reject = false;
      await page.getByRole('button', { name: 'Continue to Vibecoders' }).click();
      await page.waitForURL('https://share.test/vibecoders-ai-generation/');
      assert.deepEqual(redemptions, [token, token]);
      await page.goto(signupUrl + '/claim');
      assert.equal(await page.getByRole('button').isDisabled(), true);
      assert.deepEqual(errors, []);
      await page.close();
    }
    console.log('PASS: public image entry, username request, no automatic token use, fragment removal, explicit confirmation, errors, and desktop/mobile layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
