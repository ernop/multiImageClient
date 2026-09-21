const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../MultiImageClient/Ui/wwwroot');

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of [{ width: 1100, height: 850 }, { width: 390, height: 844 }]) {
      const page = await browser.newPage({ viewport });
      const actions = [], errors = [];
      let state = 'review', loseResponse = false;
      page.on('pageerror', error => errors.push(error.message));
      await page.route('https://admin.test/**', async route => {
        const request = route.request(), url = new URL(request.url());
        if (url.pathname === '/api/control/discord-account-requests') return route.fulfill({ json: {
          enabled: true, reviewer: 'Brouhahaha', requests: [{ id: 'a'.repeat(32), username: 'alice.discord', state,
            createdAt: Date.now(), expiresAt: Date.now() + 86400000,
            canTest: state === 'review', canSend: state === 'tested', canReject: state === 'review' || state === 'tested' }],
        } });
        if (url.pathname.startsWith('/api/control/discord-account-requests/')) {
          assert.equal(request.method(), 'POST');
          assert.equal(request.headers()['x-mic-manage'], '1');
          const action = url.pathname.split('/').at(-1); actions.push(action);
          if (action === 'test') { assert.equal(state, 'review'); state = 'tested'; }
          else if (action === 'send') { assert.equal(state, 'tested'); state = 'sent'; }
          else throw Error('Unexpected action: ' + action);
          if (loseResponse) return route.abort('failed');
          return route.fulfill({ json: { state, message: action === 'test' ? 'Test copy sent to Brouhahaha.' : 'Account link sent to @alice.discord.' } });
        }
        if (url.pathname === '/api/control/state') return route.fulfill({ json: { environments: [], accounts: [] } });
        if (url.pathname === '/api/config') return route.fulfill({ json: { generators: [] } });
        if (url.pathname === '/environment.js') return route.fulfill({ contentType: 'text/javascript', body: '' });
        const file = url.pathname.slice(1);
        assert.ok(['admin.html', 'admin.js', 'admin-discord-requests.js', 'generator-toggle.js', 'style.css'].includes(file));
        return route.fulfill({ contentType: file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript',
          body: fs.readFileSync(path.join(root, file), 'utf8') });
      });
      await page.goto('https://admin.test/admin.html');
      const reviews = page.locator('#discord-account-reviews');
      const test = reviews.getByRole('button', { name: 'Test', exact: true });
      const send = reviews.getByRole('button', { name: 'Confirmed, send to user', exact: true });
      await test.waitFor();
      assert.deepEqual(actions, []);
      assert.equal(await send.isDisabled(), true);
      await test.click();
      await reviews.getByText('Test copy sent to Brouhahaha.', { exact: true }).waitFor();
      assert.deepEqual(actions, ['test']);
      assert.equal(await test.isDisabled(), true);
      assert.equal(await send.isDisabled(), false);
      const screenshots = path.resolve(__dirname, '../.local-ui'); fs.mkdirSync(screenshots, { recursive: true });
      await page.screenshot({ path: path.join(screenshots, `discord-account-review-${viewport.width}.png`) });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      loseResponse = true;
      await send.click();
      await reviews.getByText('Account link sent', { exact: false }).waitFor();
      assert.equal(await send.isDisabled(), true);
      await reviews.getByRole('button', { name: 'Refresh requests' }).click();
      assert.deepEqual(actions, ['test', 'send']);
      assert.deepEqual(errors, []);
      await page.close();
    }
    console.log('PASS: owner Test then explicit Confirmed send, recipient display, disabled actions, lost-response recovery, desktop/mobile layout.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
