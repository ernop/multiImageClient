// Idle tabs stop polling and resume on input. See docs/ui-polling-prd.md.
// A fake page clock skips the idle period. Only GET requests reach the server.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

const IdleAfterMs = 10 * 60 * 1000;
const PausedText = 'updates paused while idle';
const PolledPaths = [
  'api/events/poll', 'api/activity/poll', 'api/logs/poll', 'api/favorites', 'api/profiles',
  'api/discord/vibecoders', 'api/status', 'api/requests', 'api/goal-loops',
];

function recordPolls(page) {
  const seen = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    const api = path.slice(path.indexOf('/api/') + 1);
    if (PolledPaths.some(prefix => api.startsWith(prefix))) seen.push(api);
  });
  return () => seen.splice(0);
}

const count = (paths, prefix) => paths.filter(path => path.startsWith(prefix)).length;

// Fake time moves in one-second steps; each step also yields real time so
// local responses arrive and in-flight guards clear.
async function run(page, ms) {
  for (let elapsed = 0; elapsed < ms; elapsed += 1000) {
    await page.clock.runFor(1000);
    await page.waitForTimeout(25);
  }
}

async function waitUntil(page, predicate, what) {
  for (let attempt = 0; attempt < 200; attempt++) {
    if (await page.evaluate(predicate)) return;
    await page.waitForTimeout(50);
  }
  throw new Error(`timed out waiting for ${what}`);
}

async function openPage(browser, url) {
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  // Never submit jobs, change server preferences, or contact paid providers.
  await context.route('**/*', route => route.request().method() === 'GET' ? route.continue() : route.abort());
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.clock.install();
  const takePolls = recordPolls(page);
  await page.goto(url);
  return { context, page, errors, takePolls };
}

async function composer(browser) {
  const { context, page, errors, takePolls } = await openPage(browser, base);
  await waitUntil(page, () => document.querySelectorAll('#gens-row .gen-toggle').length > 0, 'composer config');
  const logs = await page.locator('#logs-toggle').isVisible();
  if (logs) await page.click('#logs-toggle');

  await run(page, 30_000);
  const active = takePolls();
  assert.ok(count(active, 'api/events/poll') >= 20, `active tab polls job events: ${count(active, 'api/events/poll')}`);
  assert.ok(count(active, 'api/activity/poll') >= 5, `active tab polls activity: ${count(active, 'api/activity/poll')}`);
  if (logs) assert.ok(count(active, 'api/logs/poll') >= 20, 'open logs panel polls');

  await page.clock.fastForward(IdleAfterMs);
  await run(page, 120_000);
  assert.deepEqual(takePolls(), [], 'idle composer sends no polls');
  if (logs) assert.equal(await page.textContent('#logs-connection'), PausedText);

  await page.mouse.move(300, 300);
  await run(page, 2000);
  const resumed = takePolls();
  for (const path of ['api/events/poll', 'api/activity/poll', 'api/favorites', 'api/profiles']) {
    assert.ok(count(resumed, path) >= 1, `input resumes ${path}`);
  }
  if (logs) {
    assert.ok(count(resumed, 'api/logs/poll') >= 1, 'input resumes the logs poll');
    assert.equal(await page.textContent('#logs-connection'), 'live');
  }

  // An unfinished job of this user's keeps the event poll alive past the idle limit.
  await page.evaluate(() => {
    const card = document.createElement('div');
    card.id = 'job-idle-test-own';
    card.dataset.state = 'running';
    document.body.appendChild(card);
    ownedJobIds.add('idle-test-own');
  });
  await page.clock.fastForward(IdleAfterMs);
  await run(page, 10_000);
  assert.ok(count(takePolls(), 'api/events/poll') >= 5, 'own unfinished job keeps the event poll alive');
  await page.evaluate(() => { document.getElementById('job-idle-test-own').dataset.state = 'done'; });
  await run(page, 3000);
  takePolls();
  await run(page, 60_000);
  assert.deepEqual(takePolls(), [], 'polling stops once own work finishes');

  assert.deepEqual(errors, []);
  await context.close();
  console.log('composer: polls while active, stops when idle, resumes on input, waits for own jobs');
}

async function goalLoops(browser) {
  const { context, page, errors, takePolls } = await openPage(browser, new URL('goal.html', base).href);
  await waitUntil(page, () => typeof loopList !== 'undefined' && document.readyState === 'complete', 'goal page');
  await run(page, 1000);
  const loopId = await page.evaluate(() => (loopList.length ? loopList[0].id : null));
  if (loopId) {
    await page.evaluate(id => selectLoop(id, false), loopId);
    // The first full fetch of a large loop takes real seconds.
    await waitUntil(page, () => loopState !== null && !loopPollInFlight, 'selected loop');
  }

  await run(page, 30_000);
  const active = takePolls();
  const loopPolls = paths => paths.filter(path => path.startsWith('api/goal-loops/')).length;
  assert.ok(count(active, 'api/goal-loops') - loopPolls(active) >= 5, 'active goal page polls the list');
  if (loopId) assert.ok(loopPolls(active) >= 5, `active goal page polls the selected loop: ${loopPolls(active)}`);

  await page.clock.fastForward(IdleAfterMs);
  await run(page, 120_000);
  assert.deepEqual(takePolls(), [], 'idle goal page sends no polls');
  assert.equal(await page.textContent('#goal-running-count'), PausedText);

  await page.mouse.move(300, 300);
  await run(page, 2000);
  const resumed = takePolls();
  assert.ok(count(resumed, 'api/goal-loops') - loopPolls(resumed) >= 1, 'input resumes the list poll');
  if (loopId) assert.ok(loopPolls(resumed) >= 1, 'input resumes the loop poll');
  assert.notEqual(await page.textContent('#goal-running-count'), PausedText);

  assert.deepEqual(errors, []);
  await context.close();
  console.log(`goal loops: list${loopId ? ' and loop' : ''} polls stop when idle and resume on input`);
}

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    await composer(browser);
    await goalLoops(browser);
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
