// End-to-end Playwright test for the private admin-only Prompt Archive.
// Spawns one disposable Kestrel instance with real UiAuth (admin + normal user)
// and disposable fixture jobs — zero provider calls, zero personal data.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const { spawn } = require('node:child_process');

const root = path.resolve(__dirname, '..');
const binary = process.env.MIC_UI_TEST_BINARY || path.join(root, 'MultiImageClient/bin/Debug/net10.0/MultiImageClient.dll');
const dotnetBin = fs.existsSync(path.join(os.homedir(), '.dotnet/dotnet')) ? path.join(os.homedir(), '.dotnet/dotnet') : 'dotnet';
const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mic-prompt-archive-browser-'));

async function freePort() {
  const srv = net.createServer();
  await new Promise(resolve => srv.listen(0, '127.0.0.1', resolve));
  const port = srv.address().port;
  await new Promise(resolve => srv.close(resolve));
  return port;
}

function writeJob(savesDir, id, prompt, creator, isoDate, gens) {
  const dir = path.join(savesDir, 'UiHistory', id);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'job.json'), JSON.stringify({
    Id: id,
    Prompt: prompt,
    CreatorLogin: creator,
    CreatedAt: isoDate,
    GeneratorKeys: gens,
  }));
}

async function run() {
  let child;
  let browser;
  try {
    const port = await freePort();
    const savesDir = path.join(tmpDir, 'saves');
    fs.mkdirSync(savesDir, { recursive: true });

    // Seed 14 owner image prompts + 2 non-owner prompts (must never appear)
    for (let i = 1; i <= 14; i++) {
      const subject = i === 7
        ? 'Sunlit limestone cathedral nave with stained glass prism beams on flagstones'
        : `Bright daytime watercolor sketchbook study #${i} of alpine wildflowers and brass compass`;
      writeJob(
        savesDir,
        `owner-job-${String(i).padStart(2, '0')}`,
        subject,
        'ernieMultiZone',
        `2025-0${Math.min(9, (i % 9) + 1)}-15T12:00:00Z`,
        i % 2 === 0 ? ['gpt2'] : ['ideogram-v45']
      );
    }
    writeJob(savesDir, 'other-job-01', 'Non-owner secret prompt that must stay excluded', 'dallasVille', '2026-01-10T12:00:00Z', ['gpt2']);

    const salt = crypto.randomBytes(16);
    const hash = pw => `pbkdf2-sha256$600000$${salt.toString('base64')}$${crypto.pbkdf2Sync(pw, salt, 600000, 32, 'sha256').toString('base64')}`;
    const authPath = path.join(tmpDir, 'auth.json');
    const linksPath = path.join(tmpDir, 'links.json');
    const registryPath = path.join(tmpDir, 'registry.json');

    fs.writeFileSync(authPath, JSON.stringify({
      version: 2,
      enabled: true,
      secret: crypto.randomBytes(32).toString('hex'),
      accounts: [
        { username: 'ernieMultiZone', passwordHash: hash('admin-pass-1234') },
        { username: 'dallasVille', passwordHash: hash('member-pass-1234') },
      ],
    }));
    fs.writeFileSync(linksPath, JSON.stringify({ version: 1, accounts: [] }));
    fs.writeFileSync(registryPath, JSON.stringify({
      version: 1,
      environments: [{
        id: 'original',
        slug: 'archive-test',
        name: 'Archive Test',
        original: true,
        members: ['dallasVille'],
        goalLoops: true,
        video: true,
        promptRewrite: true,
      }],
    }));

    const settingsPath = path.join(tmpDir, 'settings.json');
    fs.writeFileSync(settingsPath, JSON.stringify({
      LogFilePath: path.join(tmpDir, 'log.txt'),
      ImageDownloadBaseFolder: savesDir,
      UiAuthFilePath: authPath,
      UiLoginLinksFilePath: linksPath,
      UiEnvironmentRegistryPath: registryPath,
      UiEnvironmentController: true,
      UiEnvironmentId: 'original',
      UiEnvironmentName: 'Archive Test',
      UiPublicBaseUrl: 'https://archive.test/archive-test',
      UiMaxConcurrentJobs: 1,
      UiMaxConcurrentGenerators: 1,
      UiMaxPendingJobs: 2,
      EnableGenerationArchive: true,
      OpenAIApiKey: 'sk-' + crypto.randomBytes(32).toString('hex'),
      EnableLocalGenerators: false,
      PromptFiles: [],
    }));

    child = spawn(dotnetBin, [binary, '--ui', '--ui-port', String(port), '--ui-no-open'], {
      cwd: tmpDir,
      env: { ...process.env, MULTIIMAGECLIENT_SETTINGS: settingsPath },
      windowsHide: true,
      stdio: 'ignore',
    });

    let ready = false;
    for (let i = 0; i < 80; i++) {
      if (child.exitCode !== null) throw new Error('Test server exited before startup.');
      try { ready = (await fetch(`http://127.0.0.1:${port}/healthz`)).ok; } catch {}
      if (ready) break;
      await new Promise(r => setTimeout(r, 250));
    }
    assert.ok(ready, 'Test server did not become healthy.');

    const browserChannel = process.env.MIC_UI_TEST_BROWSER_CHANNEL ?? 'chrome';
    browser = await chromium.launch({
      headless: true,
      ...(browserChannel === 'bundled' ? {} : { channel: browserChannel }),
    });

    async function makeContext() {
      const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 } });
      await ctx.route('https://archive.test/**', async route => {
        const req = route.request();
        const url = new URL(req.url());
        if (!url.pathname.startsWith('/archive-test/')) {
          return route.fulfill({ status: 404, body: '' });
        }
        const subPath = url.pathname.slice('/archive-test/'.length);
        const headers = { ...(await req.allHeaders()), 'x-forwarded-proto': 'https' };
        delete headers.host;
        delete headers['content-length'];
        const resp = await fetch(`http://127.0.0.1:${port}/${subPath}${url.search}`, {
          method: req.method(),
          headers,
          body: ['GET', 'HEAD'].includes(req.method()) ? undefined : req.postDataBuffer(),
          redirect: 'manual',
        });
        const respHeaders = Object.fromEntries(resp.headers);
        delete respHeaders['content-encoding'];
        delete respHeaders['content-length'];
        await route.fulfill({ status: resp.status, headers: respHeaders, body: Buffer.from(await resp.arrayBuffer()) });
      });
      return ctx;
    }

    // 1. Admin flow
    const adminCtx = await makeContext();
    const adminPage = await adminCtx.newPage();
    const adminErrors = [];
    adminPage.on('pageerror', err => adminErrors.push(err.message));

    await adminPage.goto('https://archive.test/archive-test/');
    const loginStatus = await adminPage.evaluate(async () => {
      const r = await fetch('api/auth/login', {
        method: 'POST',
        body: new URLSearchParams({ username: 'ernieMultiZone', password: 'admin-pass-1234' }),
      });
      return r.status;
    });
    assert.equal(loginStatus, 200);

    await adminPage.goto('https://archive.test/archive-test/');
    await adminPage.waitForSelector('#prompt-archive-link:not([hidden])');
    const adminCfg = await adminPage.evaluate(async () => (await fetch('api/config')).json());
    assert.ok(adminCfg.promptArchive, 'Admin /api/config must expose promptArchive.');

    // Navigate to prompt-archive.html and sync fixture jobs
    await adminPage.goto('https://archive.test/archive-test/prompt-archive.html');
    await adminPage.waitForSelector('#pa-sync-btn');
    await adminPage.click('#pa-sync-btn');
    await adminPage.waitForFunction(() => document.getElementById('stat-unique')?.textContent === '14');
    await adminPage.waitForFunction(() => document.querySelectorAll('#pa-results .pa-card').length === 10);

    // Ensure non-owner prompt never entered
    const pageText = await adminPage.textContent('#pa-results');
    assert.ok(!pageText.includes('Non-owner secret prompt'), 'Non-owner prompt leaked into archive!');

    // Verify no horizontal overflow and crisp black prompt text
    const layoutMetrics = await adminPage.evaluate(() => {
      const cardText = document.querySelector('.pa-prompt-text');
      const heading = document.querySelector('h1');
      return {
        overflow: document.documentElement.scrollWidth > window.innerWidth,
        textColor: cardText ? getComputedStyle(cardText).color : '',
        headingWeight: heading ? Number(getComputedStyle(heading).fontWeight) : 0,
      };
    });
    assert.equal(layoutMetrics.overflow, false, 'Prompt archive page overflowed horizontally.');
    assert.equal(layoutMetrics.textColor, 'rgb(0, 0, 0)', 'Prompt text must be pure black.');
    assert.ok(layoutMetrics.headingWeight >= 700, 'Heading must be bold.');

    // Reroll 10 avoids previous 10 (returns the remaining 4 unseen)
    const firstIds = await adminPage.evaluate(() => [...document.querySelectorAll('#pa-results .pa-card')].map(c => c.dataset.promptId));
    assert.equal(firstIds.length, 10);
    await adminPage.click('#pa-random-btn');
    await adminPage.waitForFunction(() => document.querySelectorAll('#pa-results .pa-card').length === 4);
    const secondIds = await adminPage.evaluate(() => [...document.querySelectorAll('#pa-results .pa-card')].map(c => c.dataset.promptId));
    assert.equal(firstIds.filter(id => secondIds.includes(id)).length, 0, 'Reroll should avoid already-seen prompts.');

    // Search FTS5 for cathedral
    await adminPage.fill('#pa-query', 'cathedral');
    await adminPage.click('#pa-search-btn');
    await adminPage.waitForFunction(() => document.querySelectorAll('#pa-results .pa-card').length === 1);
    const searchText = await adminPage.textContent('#pa-results .pa-prompt-text');
    assert.ok(searchText.includes('Sunlit limestone cathedral nave'), 'FTS5 search did not find cathedral prompt.');

    // Expand details and verify single-line occurrence table
    await adminPage.click('#pa-results .pa-card button:has-text("Details")');
    await adminPage.waitForSelector('#pa-results .pa-details-box table tbody tr');

    // Click "Try in composer" -> opens ./ and populates #prompt, strips ?archivePrompt=
    await adminPage.click('#pa-results .pa-card a:has-text("Try in composer")');
    await adminPage.waitForFunction(() => (document.getElementById('prompt')?.value || '').includes('Sunlit limestone cathedral nave'));
    const finalUrl = new URL(adminPage.url());
    assert.equal(finalUrl.searchParams.has('archivePrompt'), false, 'archivePrompt query param should be cleaned from URL.');

    // Check admin.html inline Prompt Archive section
    await adminPage.goto('https://archive.test/archive-test/admin.html');
    await adminPage.waitForFunction(() => (document.getElementById('admin-archive-summary')?.textContent || '').includes('14 unique prompts'));
    await adminPage.click('#admin-archive-random-btn');
    await adminPage.waitForFunction(() => document.querySelectorAll('#admin-archive-list .admin-pa-card').length === 10);

    assert.deepEqual(adminErrors, [], `Admin page errors: ${adminErrors.join('; ')}`);
    await adminCtx.close();

    // 2. Non-admin member flow (MUST be completely locked out)
    const memberCtx = await makeContext();
    const memberPage = await memberCtx.newPage();
    await memberPage.goto('https://archive.test/archive-test/');
    const memberLogin = await memberPage.evaluate(async () => {
      const r = await fetch('api/auth/login', {
        method: 'POST',
        body: new URLSearchParams({ username: 'dallasVille', password: 'member-pass-1234' }),
      });
      return r.status;
    });
    assert.equal(memberLogin, 200);

    await memberPage.goto('https://archive.test/archive-test/');
    await memberPage.waitForSelector('#prompt');
    const memberChecks = await memberPage.evaluate(async () => {
      const link = document.getElementById('prompt-archive-link');
      const linkHidden = !link || link.hidden || getComputedStyle(link).display === 'none';
      const cfg = await (await fetch('api/config')).json();
      const pageStatus = (await fetch('prompt-archive.html')).status;
      const statsStatus = (await fetch('api/admin/prompt-archive/stats')).status;
      const randomStatus = (await fetch('api/admin/prompt-archive/random')).status;
      const searchStatus = (await fetch('api/admin/prompt-archive/search?q=cathedral')).status;
      const itemStatus = (await fetch('api/admin/prompt-archive/items/1')).status;
      const syncStatus = (await fetch('api/admin/prompt-archive/sync', { method: 'POST', headers: { 'X-Mic-Manage': '1' } })).status;
      return { linkHidden, hasCfg: cfg.promptArchive !== null && cfg.promptArchive !== undefined, pageStatus, statsStatus, randomStatus, searchStatus, itemStatus, syncStatus };
    });

    assert.equal(memberChecks.linkHidden, true, 'Non-admin must not see #prompt-archive-link.');
    assert.equal(memberChecks.hasCfg, false, 'Non-admin /api/config must return promptArchive: null.');
    assert.equal(memberChecks.pageStatus, 403, 'Non-admin /prompt-archive.html must be 403.');
    assert.equal(memberChecks.statsStatus, 403, 'Non-admin stats must be 403.');
    assert.equal(memberChecks.randomStatus, 403, 'Non-admin random must be 403.');
    assert.equal(memberChecks.searchStatus, 403, 'Non-admin search must be 403.');
    assert.equal(memberChecks.itemStatus, 403, 'Non-admin item must be 403.');
    assert.equal(memberChecks.syncStatus, 403, 'Non-admin sync must be 403.');

    await memberCtx.close();
    console.log('PASS: prompt-archive browser E2E (admin + non-admin isolation)');
  } finally {
    if (browser) await browser.close();
    if (child && child.exitCode === null) child.kill('SIGTERM');
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

run().catch(err => {
  console.error(err);
  process.exit(1);
});
