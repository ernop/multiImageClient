// Browser test for "consider splitting into multiple images".
// Reads the local server's configuration and history; every POST is answered
// here, so no provider call, job, or message leaves the test.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../MultiImageClient/Ui/wwwroot');
const base = 'http://127.0.0.1:5960/';
const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="600"><rect width="800" height="600" fill="gold"/></svg>';
const defaultInstruction = 'The following prompt may be better served by having 2, 3, or more images. ' +
  'Please make a decision about that and return your recommendation.';
const reason = 'Twelve panels read better as three pages of four panels.';
const parts = [
  { title: 'Panels 1 to 4', prompt: 'Shared preface: ink and watercolor, bright daylight.\n\nPanels 1 to 4: the dog sees the sea.' },
  { title: 'Panels 5 to 8', prompt: 'Shared preface: ink and watercolor, bright daylight.\n\nPanels 5 to 8: the dog paddles out.' },
  { title: 'Panels 9 to 12', prompt: 'Shared preface: ink and watercolor, bright daylight.\n\nPanels 9 to 12: the dog rides a wave.' },
];
const split = index => ({
  groupId: 'fixturegrp1', exchangeId: 'fixture-exchange', index, count: 3, title: parts[index - 1].title,
  model: 'claude-opus-5-5', modelLabel: 'Opus 5.5', reason,
});

(async () => {
  const browser = await chromium.launch({ headless: true });
  const state = { split: 'plan', jobs: 'ok', splitRequests: [], jobRequests: [], held: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      if (url.origin !== new URL(base).origin) return route.abort();
      if (url.pathname === '/api/config' && request.method() === 'GET') {
        const response = await route.fetch();
        const json = await response.json();
        json.promptSplit = {
          available: true, availabilityProblem: null, model: 'claude-opus-5-5', label: 'Opus 5.5',
          defaultInstruction, maxImages: 8, maxInstructionChars: 4000, maxStyleNoteChars: 2000,
        };
        return route.fulfill({ response, json });
      }
      if (url.pathname === '/api/prompt/split' && request.method() === 'POST') {
        state.splitRequests.push(new URLSearchParams(request.postData() || ''));
        if (state.split === 'hold') { state.held.push(route); return; }
        if (state.split === 'fail') {
          return route.fulfill({ status: 502, json: {
            error: 'Opus 5.5 request failed: overloaded', exchangeId: 'fixture-failed',
            errorHint: 'Check the provider status page.', errorHintUrl: 'https://status.anthropic.com/',
          } });
        }
        const one = state.split === 'one';
        return route.fulfill({ json: {
          exchangeId: 'fixture-exchange', model: 'claude-opus-5-5', modelLabel: 'Opus 5.5', partBudget: 31000,
          imageCount: one ? 1 : 3, reason: one ? 'One clear scene fits one image.' : reason, images: one ? [] : parts,
        } });
      }
      if (url.pathname === '/api/jobs' && request.method() === 'POST') {
        state.jobRequests.push(request.postDataBuffer().toString('utf8'));
        if (state.jobs === 'busy') {
          return route.fulfill({ status: 503, json: {
            error: 'The UI queue has no room for 3 more jobs (64 pending jobs at most). Nothing was started. Try again after jobs finish.',
          } });
        }
        // Out of order on purpose: the feed must sort parts by index.
        return route.fulfill({ json: {
          jobs: [2, 3, 1].map(index => ({ id: `splitfx${index}`, split: split(index) })),
          user: 'Fixture', originalUser: 'Fixture', ownerId: '',
        } });
      }
      if (url.pathname === '/api/generator-preferences' && request.method() === 'POST') return route.fulfill({ json: {} });
      if (url.pathname.startsWith('/fixture/')) return route.fulfill({ contentType: 'image/svg+xml', body: svg });
      if (request.method() !== 'GET') return route.fulfill({ status: 400, json: { error: 'Test request captured' } });
      const relative = url.pathname.slice(1) || 'index.html';
      const file = path.resolve(root, relative);
      if (file.startsWith(root + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) {
        return route.fulfill({ path: file });
      }
      return route.continue();
    });

    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.stack));
    const ready = async () => {
      await page.waitForFunction(() => generatorPreferences !== null && promptSplitConfig?.available === true);
      await page.evaluate(() => {
        const listed = key => allGeneratorInputs().some(input => input.value === key);
        const image = generators.find(g => g.kind !== 'describe' && g.available && !g.requiresImage && listed(g.key));
        const describe = generators.find(g => g.kind === 'describe' && listed(g.key));
        for (const input of allGeneratorInputs()) {
          input.checked = input.value === image.key || input.value === describe?.key;
        }
        window.fixtureImageKey = image.key;
        window.fixtureImageLabel = genLabel(image.key);
        window.fixtureDescribeKey = describe?.key || '';
        usernameInput.value = 'Fixture';
        el('opt-n').value = '3';
        promptBox.value = 'A twelve-panel comic about a dog learning to surf.';
        promptBox.dispatchEvent(new Event('input', { bubbles: true }));
      });
    };
    const openDialog = async () => {
      await page.locator('#prompt-split').click();
      await page.waitForFunction(() => el('prompt-split-dialog').open);
    };
    const progressText = () => page.locator('#prompt-split-progress-text').textContent();

    await page.goto(base);
    await ready();
    assert.equal(await page.locator('#prompt-split').isEnabled(), true);

    // Saved dialog text: an unchanged default instruction stores as empty.
    await openDialog();
    assert.equal(await page.locator('#prompt-split-instruction').inputValue(), defaultInstruction);
    assert.equal(await page.locator('#prompt-split-style').inputValue(), '');
    const targets = await page.locator('#prompt-split-targets').textContent();
    assert.ok(targets.includes(await page.evaluate(() => fixtureImageLabel)), targets);
    assert.ok(targets.includes('The N setting does not apply.'), targets);
    if (await page.evaluate(() => fixtureDescribeKey)) {
      assert.ok(targets.includes('describe targets do not run on the parts'), targets);
    }
    await page.locator('#prompt-split-style').fill('Ink and watercolor, warm palette.');
    await page.locator('#prompt-split-cancel').click();
    await page.reload();
    await ready();
    await openDialog();
    assert.equal(await page.locator('#prompt-split-style').inputValue(), 'Ink and watercolor, warm palette.');
    assert.deepEqual(await page.evaluate(() => {
      const { splitInstruction, splitStyleNote } = buildPersonalConfiguration().promptTools;
      return { splitInstruction, splitStyleNote };
    }), { splitInstruction: '', splitStyleNote: 'Ink and watercolor, warm palette.' });
    await page.locator('#prompt-split-instruction').fill('Split only when the panels cannot fit.');
    assert.equal(await page.evaluate(() => buildPersonalConfiguration().promptTools.splitInstruction),
      'Split only when the panels cannot fit.');
    await page.locator('#prompt-split-default').click();
    assert.equal(await page.locator('#prompt-split-instruction').inputValue(), defaultInstruction);
    assert.equal(await page.evaluate(() => buildPersonalConfiguration().promptTools.splitInstruction), '');
    await page.locator('#prompt-split-cancel').click();

    // Portable configuration: round trip, version 4 migration, strict rejection.
    await page.evaluate(() => {
      const config = buildPersonalConfiguration();
      const normalized = normalizeImportedPersonalConfiguration(config);
      if (JSON.stringify(normalized.promptTools) !== JSON.stringify(config.promptTools)) throw Error('round trip');
      const old = structuredClone(config);
      old.version = 4;
      delete old.promptTools.splitInstruction;
      delete old.promptTools.splitStyleNote;
      const migrated = normalizeImportedPersonalConfiguration(old).promptTools;
      if (migrated.splitInstruction !== '' || migrated.splitStyleNote !== '') throw Error('migration');
      const changes = [
        c => { c.promptTools.splitInstruction = 42; },
        c => { c.promptTools.splitInstruction = 'x'.repeat(4001); },
        c => { c.promptTools.splitStyleNote = 'x'.repeat(2001); },
        c => { delete c.promptTools.splitStyleNote; },
        c => { c.promptTools.surprise = ''; },
        c => { c.version = 4; },
        c => { c.version = 6; },
      ];
      for (const change of changes) {
        const invalid = structuredClone(config);
        change(invalid);
        let rejected = false;
        try { normalizeImportedPersonalConfiguration(invalid); } catch { rejected = true; }
        if (!rejected) throw Error(`accepted: ${change}`);
      }
      const imported = structuredClone(config);
      imported.promptTools.splitInstruction = 'Imported instruction.';
      imported.promptTools.splitStyleNote = 'Imported style.';
      el('config-import-json').value = JSON.stringify(imported);
    });
    const reloaded = page.waitForEvent('framenavigated');
    await page.evaluate(() => { importPersonalConfiguration(); });
    await reloaded;
    await page.waitForLoadState('load');
    await ready();
    assert.deepEqual(await page.evaluate(() => ({ ...promptSplitSettings })),
      { instruction: 'Imported instruction.', styleNote: 'Imported style.' });
    await openDialog();
    assert.equal(await page.locator('#prompt-split-instruction').inputValue(), 'Imported instruction.');
    await page.locator('#prompt-split-default').click();
    await page.locator('#prompt-split-style').fill('');
    await page.locator('#prompt-split-cancel').click();

    // No image generator selected: nothing opens and the reason shows under the prompt.
    await page.evaluate(() => { for (const input of allGeneratorInputs()) input.checked = false; });
    await page.locator('#prompt-split').click();
    assert.equal(await page.evaluate(() => el('prompt-split-dialog').open), false);
    assert.match(await progressText(), /Select at least one image generator/);
    await ready();

    // A provider failure shows its message and hint; nothing is sent.
    state.split = 'fail';
    await openDialog();
    await page.locator('#prompt-split-submit').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'error');
    assert.match(await progressText(), /Opus 5\.5 request failed: overloaded Check the provider status page\. status\.anthropic\.com/);
    assert.equal(state.jobRequests.length, 0);

    // One image: the reason shows and nothing is sent.
    state.split = 'one';
    await openDialog();
    await page.locator('#prompt-split-submit').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'info');
    assert.equal(await progressText(), 'Opus 5.5 recommends one image, so nothing was sent. One clear scene fits one image.');
    assert.equal(state.jobRequests.length, 0);

    // Cancel while Opus plans: the dialog is closed, time counts, nothing is sent.
    state.split = 'hold';
    await openDialog();
    await page.locator('#prompt-split-submit').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'waiting');
    assert.equal(await page.evaluate(() => el('prompt-split-dialog').open), false);
    assert.equal(await page.locator('#prompt-split').isDisabled(), true);
    assert.match(await page.locator('#prompt-split-elapsed').textContent(), /^\d+ s$/);
    await page.locator('#prompt-split-cancel-run').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'info');
    assert.equal(await progressText(), 'Split cancelled. Nothing was sent.');
    assert.equal(await page.locator('#prompt-split').isEnabled(), true);
    assert.equal(state.jobRequests.length, 0);

    // A full queue keeps the plan; "start the jobs again" sends the same request.
    state.split = 'plan';
    state.jobs = 'busy';
    await openDialog();
    await page.locator('#prompt-split-style').fill('Ink and watercolor.');
    await page.locator('#prompt-split-submit').click();
    await page.waitForFunction(() => !el('prompt-split-retry').hidden);
    assert.match(await progressText(), /Opus 5\.5 planned 3 images, but the jobs did not start: The UI queue has no room/);
    const sent = state.splitRequests.at(-1);
    const imageKey = await page.evaluate(() => fixtureImageKey);
    assert.equal(sent.get('prompt'), 'A twelve-panel comic about a dog learning to surf.');
    assert.equal(sent.get('instruction'), defaultInstruction);
    assert.equal(sent.get('styleNote'), 'Ink and watercolor.');
    assert.equal(sent.get('user'), 'Fixture');
    assert.equal(sent.get('generators'), imageKey);
    assert.equal(sent.get('imageAttached'), 'false');
    assert.equal(typeof JSON.parse(sent.get('generatorExtraTexts')), 'object');
    state.jobs = 'ok';
    await page.locator('#prompt-split-retry').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'done');
    assert.equal(await progressText(), 'Opus 5.5 split the prompt into 3 images. Parts 1 to 3 run below, in order.');
    assert.equal(state.jobRequests.length, 2);
    const withoutBoundary = body => body.replaceAll(/-+WebKitFormBoundary\w+/g, '--boundary');
    assert.equal(withoutBoundary(state.jobRequests[0]), withoutBoundary(state.jobRequests[1]));
    const form = state.jobRequests[1];
    const field = name => (new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)\\r\\n`).exec(form) || [])[1];
    assert.equal(field('n'), '1');
    assert.equal(field('splitExchangeId'), 'fixture-exchange');
    assert.equal(field('generators'), imageKey);
    assert.equal(field('prompt'), 'A twelve-panel comic about a dog learning to surf.');
    assert.equal(field('condensedPrompts'), undefined);
    assert.equal(field('sketchComposer'), undefined);

    // The parts form one group at the top of the feed, in part order.
    const group = page.locator('#jobs > .split-group[data-split-group="fixturegrp1"]');
    assert.equal(await page.evaluate(() => el('jobs').firstElementChild.dataset.splitGroup), 'fixturegrp1');
    assert.equal(await group.locator('h3').textContent(), 'Opus 5.5 split one prompt into 3 images');
    assert.equal(await group.locator('.split-group-reason').textContent(), reason);
    const cards = group.locator('.split-group-parts > .job');
    assert.deepEqual(await cards.evaluateAll(nodes => nodes.map(node => node.dataset.jobId)), ['splitfx1', 'splitfx2', 'splitfx3']);
    assert.deepEqual(await cards.locator('.job-split-part').allTextContents(),
      ['part 1 of 3 · Panels 1 to 4', 'part 2 of 3 · Panels 5 to 8', 'part 3 of 3 · Panels 9 to 12']);
    assert.deepEqual(await cards.locator('.job-prompt').allTextContents(), parts.map(part => part.prompt));
    assert.equal(await page.evaluate(() => ['splitfx1', 'splitfx2', 'splitfx3'].every(id => ownedJobIds.has(id))), true);
    assert.equal(await page.evaluate(() => promptBox.classList.contains('prompt-not-submitted')), false);

    // A replayed announcement updates the existing card instead of adding one.
    await page.evaluate(() => addJobCard('splitfx2', 'ignored', [fixtureImageKey], false, Date.now(), 0, {
      user: 'Fixture', split: { groupId: 'fixturegrp1', exchangeId: 'fixture-exchange', index: 2, count: 3,
        title: 'Panels 5 to 8', model: 'claude-opus-5-5', modelLabel: 'Opus 5.5', reason: 'ignored' },
    }));
    assert.equal(await cards.count(), 3);

    // The viewer walks the parts in order and names the part beside the generator.
    await page.evaluate(() => {
      for (const id of ['splitfx1', 'splitfx2', 'splitfx3']) {
        applyJobEvent(id, el(`job-${id}`), {
          type: 'gen-result', gen: fixtureImageKey, ok: true, images: [`fixture/${id}.svg`],
          size: '800x600', cost: 0, ms: 1000,
        });
      }
    });
    await page.locator('#job-splitfx2 a[data-viewer-image="true"]').click();
    const viewerLabel = () => page.locator('#image-viewer-generator').textContent();
    const label = await page.evaluate(() => fixtureImageLabel);
    await page.waitForFunction(() => el('image-viewer-generator').textContent.includes('part 2 of 3'));
    assert.equal(await viewerLabel(), `${label} · part 2 of 3 · Panels 5 to 8`);
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(() => el('image-viewer-generator').textContent.includes('part 3 of 3'));
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await page.waitForFunction(() => el('image-viewer-generator').textContent.includes('part 1 of 3'));
    assert.equal(await viewerLabel(), `${label} · part 1 of 3 · Panels 1 to 4`);
    await page.keyboard.press('Escape');

    // A group whose parts are all filtered out disappears with them.
    await page.evaluate(() => {
      for (const id of ['splitfx1', 'splitfx2', 'splitfx3']) el(`job-${id}`).classList.add('night-hidden');
    });
    assert.equal(await group.evaluate(node => getComputedStyle(node).display), 'none');
    await page.evaluate(() => el('job-splitfx2').classList.remove('night-hidden'));
    assert.equal(await group.evaluate(node => getComputedStyle(node).display), 'flex');

    await page.evaluate(() => {
      for (const id of ['splitfx1', 'splitfx3']) el(`job-${id}`).classList.remove('night-hidden');
    });
    await page.locator('#prompt-split-progress').screenshot({ path: '/tmp/prompt-split-progress.png' });
    const groupBox = await page.evaluate(() => {
      const header = document.querySelector('body > header').getBoundingClientRect().bottom;
      const node = document.querySelector('.split-group[data-split-group="fixturegrp1"]');
      window.scrollTo(0, node.getBoundingClientRect().top + window.scrollY - header - 12);
      const box = node.getBoundingClientRect();
      return { x: box.left, y: box.top, width: box.width, height: Math.min(box.height, window.innerHeight - box.top) };
    });
    await page.screenshot({ path: '/tmp/prompt-split-group.png', clip: groupBox });
    await openDialog();
    await page.locator('#prompt-split-dialog').screenshot({ path: '/tmp/prompt-split-dialog.png' });
    await page.locator('#prompt-split-cancel').click();
    assert.deepEqual(errors, []);
    console.log('PASS: split dialog text persistence and import, failure, one-image, cancel, retry, part grouping, and viewer order.');
  } finally {
    for (const route of state.held) await route.abort().catch(() => {});
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
