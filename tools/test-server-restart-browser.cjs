// A new server process of the same committed build must not reload an open
// composer: the page adopts the new process token and replays today's events
// in place. A changed or uncommitted build, or an ended login, still reloads.
// See docs/ui-polling-prd.md. The page's process token is replaced with a stale
// one, so the server answers exactly as after a restart. Only GET requests
// reach the server.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

const StaleInstance = 'f'.repeat(32);
// The page compares build identities only, so a fixed committed-looking value
// keeps these checks independent of the local server's own working tree.
const CommittedBuild = 'abcdef123456';

async function waitUntil(page, predicate, what, arg) {
  for (let attempt = 0; attempt < 400; attempt++) {
    if (await page.evaluate(predicate, arg)) return;
    await page.waitForTimeout(50);
  }
  throw new Error(`timed out waiting for ${what}`);
}

// Everything a non-idempotent replay could duplicate or regress, per job and cell.
function feedSignature() {
  return [...document.querySelectorAll('#jobs .job')].map(card => [
    card.id,
    card.dataset.state,
    card.querySelectorAll('.grid-link, .describe-sheet-link, .job-goal-loop, .job-condensed-prompt, .job-input-count').length,
    ...[...card.querySelectorAll('.cell')].map(cell => [
      cell.dataset.gen,
      cell.dataset.state,
      cell.querySelectorAll('.media-result').length,
      cell.querySelectorAll('.cell-hint').length,
      cell.querySelector('.cell-status').textContent,
    ].join(':')),
  ].join(' | '));
}

// editConfig(json) edits each /api/config reply or returns an HTTP status to send instead.
async function openComposer(browser, editConfig) {
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  // Never submit jobs, change server preferences, or contact paid providers.
  await context.route('**/*', route => route.request().method() === 'GET' ? route.continue() : route.abort());
  await context.route('**/api/config', async route => {
    const response = await route.fetch();
    const json = await response.json();
    const status = editConfig(json);
    if (status) return route.fulfill({ status, json: { error: 'test' } });
    return route.fulfill({ response, json });
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const stats = { rejectedPolls: 0, configReads: 0 };
  page.on('response', response => {
    const path = new URL(response.url()).pathname;
    if (path.endsWith('/api/events/poll') && response.status() === 401) stats.rejectedPolls++;
    if (path.endsWith('/api/config')) stats.configReads++;
  });
  await page.goto(base);
  await waitUntil(page,
    () => document.querySelectorAll('#gens-row .gen-toggle').length > 0 && jobActivityHydrated,
    'hydrated composer');
  await page.evaluate(() => { window.sameDocument = true; });
  return { context, page, errors, stats };
}

const committed = json => { json.build.commit = CommittedBuild; };
const simulateRestart = page => page.evaluate(stale => { clientInstanceId = stale; }, StaleInstance);

async function expectReplayInPlace(page, stats) {
  const real = await page.evaluate(() => clientInstanceId);
  const rejectedBefore = stats.rejectedPolls;
  await simulateRestart(page);
  await waitUntil(page, real => clientInstanceId === real && jobActivityHydrated, 'in-place replay', real);
  assert.ok(stats.rejectedPolls > rejectedBefore, 'the server rejected the stale process token');
  assert.equal(await page.evaluate(() => window.sameDocument), true, 'the page did not reload');
}

async function expectReload(page, what) {
  const loaded = page.waitForEvent('domcontentloaded', { timeout: 20_000 });
  await simulateRestart(page);
  await loaded;
  assert.equal(await page.evaluate(() => window.sameDocument), undefined, `${what} reloads the page`);
}

async function sameBuild(browser) {
  const { context, page, errors, stats } = await openComposer(browser, committed);
  const logs = await page.locator('#logs-toggle').isVisible();
  if (logs) {
    await page.click('#logs-toggle');
    await waitUntil(page, () => lastLogSequence > 0, 'log lines');
  }
  const before = await page.evaluate(feedSignature);
  const logsGeneration = await page.evaluate(() => logsProcessGeneration);
  const configReads = stats.configReads;

  await expectReplayInPlace(page, stats);
  assert.equal(stats.configReads, configReads + 1, 'one config read decides the replay');
  assert.deepEqual(await page.evaluate(feedSignature), before, 'the replay leaves every card unchanged');
  if (logs) {
    assert.equal(await page.evaluate(() => logsProcessGeneration), logsGeneration + 1, 'the logs restart once');
    await waitUntil(page, () => lastLogSequence > 0, 'log lines from the new process');
  }
  // Today's feed may hold no failure with a provider hint; a page-only card does.
  const hints = await page.evaluate(() => {
    const id = 'restart-test-hint';
    const card = addJobCard(id, 'restart test', ['gpt2'], false, Date.now(), 0, {});
    const failed = { type: 'gen-result', gen: 'gpt2', ok: false, error: 'test failure', errorHint: 'Test hint.', images: [] };
    applyJobEvent(id, card, failed);
    applyJobEvent(id, card, failed);
    const count = card.querySelectorAll('.cell-hint').length;
    card.remove();
    return count;
  });
  assert.equal(hints, 1, 'a replayed failure keeps one provider hint');
  assert.deepEqual(errors, []);
  await context.close();
  console.log(`same committed build: replays ${before.length} cards in place without reloading`);
}

async function failedConfigRead(browser) {
  let failNext = false;
  const { context, page, errors, stats } = await openComposer(browser, json => {
    committed(json);
    if (!failNext) return 0;
    failNext = false;
    return 502;
  });
  failNext = true;
  await expectReplayInPlace(page, stats);
  assert.equal(failNext, false, 'one config read failed first');
  assert.deepEqual(errors, []);
  await context.close();
  console.log('failed config read: retries, then replays in place');
}

async function changedBuild(browser) {
  const { context, page } = await openComposer(browser, committed);
  await page.evaluate(() => { clientBuildCommit = '0123456789ab'; });
  await expectReload(page, 'a changed build');
  await context.close();
  console.log('changed build: reloads');
}

async function uncommittedBuild(browser) {
  const { context, page } = await openComposer(browser, json => { json.build.commit = `${CommittedBuild}-dirty`; });
  await expectReload(page, 'an uncommitted build');
  await context.close();
  console.log('uncommitted build: reloads');
}

async function endedLogin(browser) {
  let rejectNext = false;
  const { context, page } = await openComposer(browser, json => {
    committed(json);
    if (!rejectNext) return 0;
    rejectNext = false;
    return 401;
  });
  rejectNext = true;
  await expectReload(page, 'an ended login');
  await context.close();
  console.log('ended login: reloads');
}

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    await sameBuild(browser);
    await failedConfigRead(browser);
    await changedBuild(browser);
    await uncommittedBuild(browser);
    await endedLogin(browser);
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
