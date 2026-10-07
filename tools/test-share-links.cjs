// Runs one real Kestrel process with disposable data and no provider calls.
// The browser proxy maps /one/ to the private site and /shared/one/ to /public/, as nginx does.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const zlib = require('node:zlib');
const { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const binary = process.env.MIC_UI_TEST_BINARY || path.join(root, 'MultiImageClient/bin/Debug/net10.0/MultiImageClient.dll');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'mic-share-links-'));
const site = 'https://environment.test';
const redirects = [];
let child;

function png(width, height, [r, g, b]) {
  const row = Buffer.alloc(1 + width * 3);
  for (let x = 0; x < width; x++) row.set([r, g, b], 1 + x * 3);
  const chunk = (type, data) => {
    const length = Buffer.alloc(4); length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
  const pixels = zlib.deflateSync(Buffer.concat(Array.from({ length: height }, () => row)));
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', pixels), chunk('IEND', Buffer.alloc(0))]);
}

function seedJob(saves, id, prompt, createdAt, colors) {
  const folder = path.join(saves, 'UiHistory', id);
  fs.mkdirSync(folder, { recursive: true });
  const images = {};
  const urls = colors.map((color, index) => {
    const file = path.join(folder, `gpt2-${index}.png`);
    fs.writeFileSync(file, png(640, 480, color));
    images[`gpt2/${index}`] = { Path: file, ContentType: 'image/png', ContentSha256: '', CdnKey: '', CdnFileId: '' };
    return `/api/jobs/${id}/images/gpt2/${index}`;
  });
  fs.writeFileSync(path.join(folder, 'images.json'), JSON.stringify(images));
  fs.writeFileSync(path.join(folder, 'job.json'), JSON.stringify({ Id: id, Prompt: prompt, CreatedBy: 'ernie', CreatorLogin: 'ernieMultiZone',
    InputImagePath: '', InputImagePaths: [], InputImageWidth: 0, InputImageHeight: 0, GeneratorKeys: ['gpt2'], CreatedAt: createdAt,
    SourceJobId: '', SourceGenerator: '', SourceIndex: null, Done: true }));
  const at = Date.parse(createdAt);
  const events = [
    { type: 'accepted', gens: ['gpt2'], hasImage: false, inputCount: 0, inputWidth: null, inputHeight: null, shape: 'auto',
      detail: 'standard', quality: 'low', moderation: 'low', n: colors.length, generatorExtraTexts: {} },
    { type: 'job-start', at },
    { type: 'gen-start', gen: 'gpt2', at },
    { type: 'gen-result', gen: 'gpt2', ok: true, error: '', ms: 900, images: urls, thumbs: urls.map(url => url + '?thumb=1'),
      label: 'gpt-image-2 640x480', size: '640x480', cost: 0 },
    { type: 'job-done' },
  ];
  fs.writeFileSync(path.join(folder, 'events.jsonl'), events.map(event => JSON.stringify(event)).join('\n') + '\n');
}

async function freePort() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}

(async () => {
  let browser;
  try {
    const saves = path.join(temporary, 'saves');
    const liveJob = crypto.randomBytes(6).toString('hex'), archivedJob = crypto.randomBytes(6).toString('hex');
    const livePrompt = 'A bright orange kite over a green field at noon.';
    const archivedPrompt = 'Two blue sailboats on a clear lake in daylight.';
    seedJob(saves, liveJob, livePrompt, new Date().toISOString(), [[240, 140, 40], [40, 160, 90]]);
    seedJob(saves, archivedJob, archivedPrompt, new Date(Date.now() - 3 * 86400000).toISOString(), [[40, 90, 220], [220, 220, 60]]);
    const shareToken = crypto.randomBytes(32).toString('hex');
    fs.mkdirSync(path.join(saves, 'UiPublicShares'), { recursive: true });
    fs.writeFileSync(path.join(saves, 'UiPublicShares', shareToken + '.json'), JSON.stringify({ Version: 1, Token: shareToken, IsPage: false,
      PageToken: '', JobId: liveJob, Generator: 'gpt2', ImageIndex: 0, Owner: 'ernieMultiZone', State: 'sent', CreatedAt: Date.now(), ExpiresAt: Date.now(),
      DestinationHash: '', GuildId: '', ChannelId: '', ServerName: '', ChannelName: '', PublicUrl: '', ThreadDay: '', ThreadName: '',
      Snapshot: { Prompt: livePrompt, Assets: [{ Kind: 'image', Generator: 'gpt2', Index: 0, Label: 'gpt2 · 1' }], Additions: [], TextOutputs: [] } }));

    const salt = crypto.randomBytes(16);
    const hash = password => `pbkdf2-sha256$600000$${salt.toString('base64')}$${crypto.pbkdf2Sync(password, salt, 600000, 32, 'sha256').toString('base64')}`;
    fs.writeFileSync(path.join(temporary, 'auth.json'), JSON.stringify({ version: 2, enabled: true, secret: crypto.randomBytes(32).toString('hex'),
      accounts: ['ernieMultiZone', 'victor', 'bob'].map(username => ({ username, passwordHash: hash(username + '-password') })) }));
    fs.writeFileSync(path.join(temporary, 'links.json'), JSON.stringify({ version: 1, accounts: [] }));
    fs.writeFileSync(path.join(temporary, 'registry.json'), JSON.stringify({ version: 1, environments: [
      { id: 'one', slug: 'one', name: 'Original', original: true, members: ['victor'], goalLoops: true, video: true, promptRewrite: true }] }));
    const port = await freePort();
    const settings = { LogFilePath: path.join(temporary, 'log.txt'), ImageDownloadBaseFolder: saves,
      UiAuthFilePath: path.join(temporary, 'auth.json'), UiLoginLinksFilePath: path.join(temporary, 'links.json'),
      UiEnvironmentRegistryPath: path.join(temporary, 'registry.json'), UiEnvironmentController: true,
      UiEnvironmentId: 'one', UiEnvironmentName: 'Original', UiPublicBaseUrl: `${site}/one`, UiPublicShareBaseUrl: `${site}/shared/one`,
      UiMaxConcurrentJobs: 1, UiMaxConcurrentGenerators: 1, UiMaxPendingJobs: 2, EnableGenerationArchive: false,
      OpenAIApiKey: 'sk-' + crypto.randomBytes(32).toString('hex'), EnableLocalGenerators: false, PromptFiles: [] };
    fs.writeFileSync(path.join(temporary, 'settings.json'), JSON.stringify(settings));
    child = spawn('dotnet', [binary, '--ui', '--ui-port', String(port), '--ui-no-open'], { cwd: temporary,
      env: { ...process.env, MULTIIMAGECLIENT_SETTINGS: path.join(temporary, 'settings.json') }, windowsHide: true, stdio: 'ignore' });
    let ready = false;
    for (let i = 0; i < 80 && !ready; i++) {
      if (child.exitCode !== null) throw new Error('The server exited before startup.');
      try { ready = (await fetch(`http://127.0.0.1:${port}/healthz`)).ok; } catch {}
      if (!ready) await new Promise(resolve => setTimeout(resolve, 250));
    }
    assert(ready, 'The server did not start.');

    const channel = process.env.MIC_UI_TEST_BROWSER_CHANNEL ?? 'chrome';
    browser = await chromium.launch({ headless: true, ...(channel === 'bundled' ? {} : { channel }) });
    async function context() {
      const context = await browser.newContext({ viewport: { width: 1100, height: 850 } });
      await context.route(`${site}/**`, async route => {
        const request = route.request(), url = new URL(request.url());
        const target = url.pathname.startsWith('/one/') ? url.pathname.slice('/one'.length)
          : url.pathname.startsWith('/shared/one/') ? '/public/' + url.pathname.slice('/shared/one/'.length) : null;
        if (target === null) return route.fulfill({ status: 404, body: '' });
        const headers = { ...await request.allHeaders(), 'x-forwarded-proto': 'https' };
        delete headers.host; delete headers['content-length'];
        const response = await fetch(`http://127.0.0.1:${port}${target}${url.search}`, { method: request.method(), headers,
          body: ['GET', 'HEAD'].includes(request.method()) ? undefined : request.postDataBuffer(), redirect: 'manual' });
        // Playwright does not route the request that follows a fulfilled redirect, so the proxy records it and navigates.
        const location = response.headers.get('location');
        if (response.status >= 300 && response.status < 400 && location?.startsWith(`${site}/`)) {
          redirects.push([url.pathname + url.search, location]);
          return route.fulfill({ status: 200, contentType: 'text/html', body: `<script>location.replace(${JSON.stringify(location)})</script>` });
        }
        const resultHeaders = Object.fromEntries(response.headers);
        delete resultHeaders['content-encoding']; delete resultHeaders['content-length'];
        await route.fulfill({ status: response.status, headers: resultHeaders, body: Buffer.from(await response.arrayBuffer()) });
      });
      context.on('page', opened => {
        opened.on('pageerror', error => console.error('browser page error:', error.message));
        opened.on('console', message => {
          if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) console.error('browser console:', message.text());
        });
      });
      return context;
    }
    const hasSession = async context => (await context.cookies()).some(cookie => cookie.name === 'mic_auth');

    // The owner makes links from a job card and from the viewer. Nothing is sent.
    const owner = await context();
    await owner.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: site });
    const page = await owner.newPage();
    const sends = [];
    page.on('request', request => { if (/discord|vibecoders|fablebot/i.test(request.url())) sends.push(request.url()); });
    await page.goto(`${site}/one/`);
    assert.equal(await page.evaluate(async () => (await fetch('api/auth/login', { method: 'POST',
      body: new URLSearchParams({ username: 'ernieMultiZone', password: 'ernieMultiZone-password' }) })).status), 200);
    await page.goto(`${site}/one/`);
    const card = page.locator(`#job-${liveJob}`);
    await card.locator('a[data-viewer-image="true"]').first().waitFor();
    const dialog = page.locator('#share-link-dialog'), linkField = page.locator('#share-link-url');
    await card.locator('.job-share-link').click();
    await page.waitForFunction(() => document.getElementById('share-link-url').value !== '');
    const promptLink = `${site}/shared/one/prompt/${liveJob}`;
    assert.equal(await linkField.inputValue(), promptLink);
    assert.equal(await dialog.locator('.share-link-scope').innerText(),
      'Members of Original can open this link. Signed-in members go directly to this prompt. Other people log in first.');
    assert.equal(await dialog.locator('.share-link-prompt').innerText(), livePrompt);
    assert.equal(await dialog.locator('.share-link-thumb').isHidden(), true);
    await dialog.getByRole('button', { name: 'Copy link' }).click();
    await dialog.getByText('Link copied.', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), promptLink);
    for (const [width, height] of [[1100, 850], [390, 844]]) {
      await page.setViewportSize({ width, height });
      assert.ok(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth));
      await page.screenshot({ path: path.join(temporary, `dialog-prompt-${width}.png`) });
    }
    await page.setViewportSize({ width: 1100, height: 850 });
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(await linkField.inputValue(), '');
    assert.equal(await dialog.locator('.share-link-prompt').innerText(), '');

    await card.locator('a[data-viewer-image="true"]').first().click();
    await page.locator('#image-viewer').waitFor({ state: 'visible' });
    await page.locator('#image-viewer-share-link').waitFor({ state: 'visible' });
    const position = await page.locator('#image-viewer-position').innerText();
    await page.locator('#image-viewer-share-link').click();
    await page.waitForFunction(() => document.getElementById('share-link-url').value !== '');
    assert.equal(await linkField.inputValue(), `${promptLink}?gen=gpt2&n=0`);
    assert.match(await dialog.locator('.share-link-scope').innerText(), /go directly to this image\./);
    await dialog.locator('.share-link-thumb').waitFor({ state: 'visible' });
    await page.screenshot({ path: path.join(temporary, 'dialog-image-1100.png') });
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('KeyV');
    assert.equal(await page.locator('#image-viewer-position').innerText(), position);
    assert.equal(await page.locator('#image-viewer-favorite').getAttribute('aria-pressed'), 'false');
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(await page.locator('#image-viewer').isVisible(), true);
    assert.deepEqual(sends, []);

    // A visitor sees only the login form, then lands on the exact archived image.
    const visitor = await context();
    const anonymous = await visitor.newPage();
    const imageLink = `${site}/shared/one/prompt/${archivedJob}?gen=gpt2&n=1`;
    const loginResponse = await anonymous.goto(imageLink);
    assert.equal(loginResponse.status(), 200);
    const loginHtml = await loginResponse.text();
    for (const secret of [`${site}/one`, archivedPrompt, livePrompt]) assert.equal(loginHtml.includes(secret), false);
    await anonymous.getByRole('heading', { name: 'Log in to Original' }).waitFor();
    await anonymous.waitForFunction(() => !document.querySelector('#login button').disabled);
    for (const [width, height] of [[1100, 850], [390, 844]]) {
      await anonymous.setViewportSize({ width, height });
      await anonymous.screenshot({ path: path.join(temporary, `login-${width}.png`) });
    }
    async function logIn(target, username, password, button) {
      await target.getByLabel('Username').fill(username);
      await target.getByLabel('Password').fill(password);
      await target.getByRole('button', { name: button }).click();
    }
    await logIn(anonymous, 'victor', 'wrong-password', 'Log in and open');
    await anonymous.locator('#status.error').getByText('Wrong username or password.', { exact: true }).waitFor();
    await logIn(anonymous, 'bob', 'bob-password', 'Log in and open');
    await anonymous.locator('#status.error')
      .getByText('This account cannot open Original. Log in with an account that has access.', { exact: true }).waitFor();
    assert.equal(await hasSession(visitor), false);
    await anonymous.screenshot({ path: path.join(temporary, 'login-error-390.png') });
    await anonymous.setViewportSize({ width: 1100, height: 850 });
    await logIn(anonymous, 'victor', 'victor-password', 'Log in and open');
    await anonymous.waitForURL(`${site}/one/?job=${archivedJob}&gen=gpt2&n=1`);
    await anonymous.locator('#image-viewer').waitFor({ state: 'visible' });
    await anonymous.waitForFunction(id => (localStorage.getItem('mic_viewer_seen_v1') || '').includes(`${id}|gpt2|1`), archivedJob);
    assert.equal(await hasSession(visitor), true);
    await anonymous.screenshot({ path: path.join(temporary, 'landed-1100.png') });

    // A signed-in member skips the form.
    await anonymous.goto(promptLink);
    await anonymous.waitForURL(`${site}/one/?job=${liveJob}`);
    assert.deepEqual(redirects.at(-1), [`/shared/one/prompt/${liveJob}`, `${site}/one/?job=${liveJob}`]);
    await anonymous.locator(`#job-${liveJob}`).waitFor();
    assert.equal(await anonymous.locator('#image-viewer').isVisible(), false);
    await anonymous.goto(`${promptLink}?gen=gpt2&n=7`);
    await anonymous.locator('#send-error').getByText('the linked result is no longer available', { exact: false }).waitFor();
    assert.equal((await anonymous.goto(`${promptLink}/`)).status(), 404);

    // "Make your own" logs in from the public page and opens the composer.
    const reuseContext = await context();
    const reuse = await reuseContext.newPage();
    await reuse.goto(`${site}/shared/one/${shareToken}/reuse`);
    await reuse.waitForFunction(() => !document.querySelector('#login button').disabled);
    await logIn(reuse, 'victor', 'wrong-password', 'Log in and make your own');
    await reuse.locator('#status.error').getByText('Login failed or this account lacks access. Ask Ernie in Discord.', { exact: true }).waitFor();
    await logIn(reuse, 'victor', 'victor-password', 'Log in and make your own');
    await reuse.waitForURL(`${site}/one/?shared=${shareToken}`);
    await reuse.waitForFunction(prompt => document.getElementById('prompt').value === prompt, livePrompt);

    console.log('PASS: card and viewer share links; copy without sending; dialog blocks viewer keys; anonymous login page hides the private address and prompt; '
      + 'wrong password and non-member keep no session; member login opens the exact archived image; signed-in members skip the form; '
      + 'missing results report an error; Make your own logs in through the browser.');
    console.log('Artifacts: ' + temporary);
  } finally {
    if (browser) await browser.close();
    if (child && child.exitCode === null) { const exited = new Promise(resolve => child.once('exit', resolve)); child.kill(); await exited; }
  }
})().catch(error => { console.error(error.stack); console.error('Artifacts: ' + temporary); process.exitCode = 1; });
