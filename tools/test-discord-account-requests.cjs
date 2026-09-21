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
function headers(html) {
  const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
  const hash = crypto.createHash('sha256').update(script).digest('base64');
  return { 'referrer-policy': 'no-referrer',
    'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'self'; script-src 'sha256-" + hash + "'" };
}
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
      let reject = true, requestOutcome = 'error';
      await page.route('https://share.test/**', async route => {
        const request = route.request(), url = request.url();
        assert.ok(!url.includes(token), 'A token entered an HTTP URL.');
        if (url === publicUrl) return route.fulfill({ contentType: 'text/html', body: publicHtml });
        if (url.startsWith(publicUrl + 'asset/')) return route.fulfill({ contentType: 'image/svg+xml',
          body: '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#ccd"/></svg>' });
        if (url === signupUrl + '/request') {
          assert.equal(request.method(), 'POST');
          assert.equal(request.headers().origin, 'https://share.test');
          assert.equal(request.headers()['x-mic-account'], '1');
          assert.equal(request.headers().referer, undefined);
          const fields = new URLSearchParams(request.postData());
          requests.push(fields.get('username'));
          if (requestOutcome === 'unavailable') return route.fulfill({ status: 503, contentType: 'text/html', body: '<p>Unavailable</p>' });
          return route.fulfill({ status: requestOutcome === 'error' ? 400 : 200, json: requestOutcome === 'error'
            ? { error: 'No exact Discord username matched.' } : { state: 'review', message: 'Your request is waiting for Ernie to review.' } });
        }
        if (url === signupUrl + '/claim' && request.method() === 'POST') {
          assert.equal(request.headers().origin, 'https://share.test');
          redemptions.push(new URLSearchParams(request.postData()).get('token'));
          return route.fulfill({ status: reject ? 400 : 200, json: reject
            ? { error: 'This account link is invalid, expired, or already used.' }
            : { destination: 'https://share.test/vibecoders-ai-generation/' } });
        }
        if (url === signupUrl + '/claim') return route.fulfill({ contentType: 'text/html', body: claimHtml,
          headers: headers(claimHtml) });
        if (url === signupUrl) return route.fulfill({ contentType: 'text/html', body: requestHtml, headers: headers(requestHtml) });
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
      await page.getByRole('button', { name: 'Request account', exact: true }).click();
      await page.getByText('No exact Discord username matched.').waitFor();
      assert.equal(page.url(), signupUrl);
      assert.deepEqual(requests, ['alice.discord']);
      requestOutcome = 'unavailable';
      await page.getByRole('button', { name: 'Request account', exact: true }).click();
      await page.getByText('The server could not process this request. Try again later.').waitFor();
      assert.equal(page.url(), signupUrl);
      requestOutcome = 'success';
      await page.getByRole('button', { name: 'Request account', exact: true }).click();
      await page.getByText('Your request is waiting for Ernie to review.').waitFor();
      assert.deepEqual(requests, ['alice.discord', 'alice.discord', 'alice.discord']);
      assert.equal(page.url(), signupUrl);
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
