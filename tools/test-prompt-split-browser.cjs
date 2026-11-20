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
        const images = generators
          .filter(g => g.kind !== 'describe' && g.available && !g.requiresImage && listed(g.key))
          .slice(0, 2);
        if (images.length < 2) throw new Error('This test needs two available, listed image generators.');
        const describe = generators.find(g => g.kind === 'describe' && listed(g.key));
        for (const input of allGeneratorInputs()) {
          input.checked = images.some(g => g.key === input.value) || input.value === describe?.key;
        }
        window.fixtureImageKeys = checkedGeneratorKeys().filter(key => !isDescribeGenKey(key));
        window.fixtureImageLabels = fixtureImageKeys.map(genLabel);
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
    for (const label of await page.evaluate(() => fixtureImageLabels)) assert.ok(targets.includes(label), targets);
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
      delete old.promptTools.models;
      const migrated = normalizeImportedPersonalConfiguration(old).promptTools;
      if (migrated.splitInstruction !== '' || migrated.splitStyleNote !== '' || migrated.models.split !== '') throw Error('migration');
      const changes = [
        c => { c.promptTools.splitInstruction = 42; },
        c => { c.promptTools.splitInstruction = 'x'.repeat(4001); },
        c => { c.promptTools.splitStyleNote = 'x'.repeat(2001); },
        c => { delete c.promptTools.splitStyleNote; },
        c => { delete c.promptTools.models; },
        c => { c.promptTools.models.advice = 'not-a-real-model'; },
        c => { c.promptTools.surprise = ''; },
        c => { c.version = 4; },
        c => { c.version = 7; },
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
    const imageKeys = await page.evaluate(() => fixtureImageKeys.join(','));
    assert.equal(sent.get('prompt'), 'A twelve-panel comic about a dog learning to surf.');
    assert.equal(sent.get('instruction'), defaultInstruction);
    assert.equal(sent.get('styleNote'), 'Ink and watercolor.');
    assert.equal(sent.get('user'), 'Fixture');
    assert.equal(sent.get('generators'), imageKeys);
    assert.equal(sent.get('imageAttached'), 'false');
    assert.equal(typeof JSON.parse(sent.get('generatorExtraTexts')), 'object');
    state.jobs = 'ok';
    await page.locator('#prompt-split-retry').click();
    await page.waitForFunction(() => el('prompt-split-progress').dataset.mode === 'done');
    assert.equal(await progressText(),
      'Opus 5.5 split the prompt into 3 images. Below, each image generator shows parts 1 to 3 in order.');
    assert.equal(state.jobRequests.length, 2);
    const withoutBoundary = body => body.replaceAll(/-+WebKitFormBoundary\w+/g, '--boundary');
    assert.equal(withoutBoundary(state.jobRequests[0]), withoutBoundary(state.jobRequests[1]));
    const form = state.jobRequests[1];
    const field = name => (new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)\\r\\n`).exec(form) || [])[1];
    assert.equal(field('n'), '1');
    assert.equal(field('splitExchangeId'), 'fixture-exchange');
    assert.equal(field('generators'), imageKeys);
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
    await page.evaluate(() => addJobCard('splitfx2', 'ignored', fixtureImageKeys, false, Date.now(), 0, {
      user: 'Fixture', split: { groupId: 'fixturegrp1', exchangeId: 'fixture-exchange', index: 2, count: 3,
        title: 'Panels 5 to 8', model: 'claude-opus-5-5', modelLabel: 'Opus 5.5', reason: 'ignored' },
    }));
    assert.equal(await cards.count(), 3);

    // Each image generator gets one row: its heading, then its parts in part order.
    const [keyA, keyB] = await page.evaluate(() => fixtureImageKeys);
    const [labelA, labelB] = await page.evaluate(() => fixtureImageLabels);
    const makers = group.locator('.split-maker');
    assert.deepEqual(await makers.evaluateAll(nodes => nodes.map(node => node.dataset.gen)), [keyA, keyB]);
    assert.deepEqual(await makers.allTextContents(), [labelA, labelB]);
    for (const key of [keyA, keyB]) {
      assert.deepEqual(await group.locator(`.cell[data-gen="${key}"] .cell-part`).allTextContents(),
        ['part 1', 'part 2', 'part 3']);
    }
    // Generator A returns two images for part 1; every other cell returns one.
    await page.evaluate(() => {
      for (const id of ['splitfx1', 'splitfx2', 'splitfx3']) {
        for (const gen of fixtureImageKeys) {
          const count = id === 'splitfx1' && gen === fixtureImageKeys[0] ? 2 : 1;
          applyJobEvent(id, el(`job-${id}`), {
            type: 'gen-result', gen, ok: true, size: '800x600', cost: 0, ms: 1000,
            images: Array.from({ length: count }, (_, n) => `fixture/${id}-${gen}-${n}.svg`),
          });
        }
      }
    });

    const layout = () => page.evaluate(keys => {
      const box = node => {
        const rect = node.getBoundingClientRect();
        return { left: Math.round(rect.left), top: Math.round(rect.top), bottom: Math.round(rect.bottom) };
      };
      const grid = document.querySelector('.split-group[data-split-group="fixturegrp1"] .split-group-parts');
      const ids = ['splitfx1', 'splitfx2', 'splitfx3'];
      return {
        heads: ids.map(id => box(el(`job-${id}`).querySelector('.split-part-head'))),
        makers: keys.map(key => box(grid.querySelector(`.split-maker[data-gen="${key}"]`))),
        rows: keys.map(key => ids.map(id => box(el(`job-${id}`).querySelector(`.cell[data-gen="${key}"]`)))),
      };
    }, [keyA, keyB]);
    const waitForColumns = columns => page.waitForFunction(value =>
      document.querySelector('.split-group[data-split-group="fixturegrp1"] .split-group-parts').dataset.columns === value,
    String(columns));

    // Wide screen: the part heads share one line, and each generator row puts part i under head i.
    await waitForColumns(3);
    const wide = await layout();
    assert.ok(wide.heads.every(head => head.top === wide.heads[0].top), JSON.stringify(wide.heads));
    assert.ok(wide.heads[0].left < wide.heads[1].left && wide.heads[1].left < wide.heads[2].left);
    let above = Math.max(...wide.heads.map(head => head.bottom));
    for (const [index, row] of wide.rows.entries()) {
      assert.ok(wide.makers[index].top >= above, `generator ${index + 1} heading sits below the line above it`);
      assert.ok(row.every(cell => cell.top === row[0].top && cell.top >= wide.makers[index].bottom), JSON.stringify(row));
      assert.deepEqual(row.map(cell => cell.left), wide.heads.map(head => head.left));
      above = Math.max(...row.map(cell => cell.bottom));
    }

    // Narrower screen: two columns, so part 3 wraps under part 1 in every row.
    await page.setViewportSize({ width: 760, height: 1000 });
    await waitForColumns(2);
    const medium = await layout();
    assert.equal(medium.heads[1].top, medium.heads[0].top);
    assert.equal(medium.heads[2].left, medium.heads[0].left);
    assert.ok(medium.heads[2].top >= medium.heads[0].bottom);
    above = Math.max(...medium.heads.map(head => head.bottom));
    for (const [index, row] of medium.rows.entries()) {
      assert.ok(medium.makers[index].top >= above, `generator ${index + 1} heading sits below the line above it`);
      assert.equal(row[1].top, row[0].top);
      assert.equal(row[2].left, row[0].left);
      assert.ok(row[2].top >= row[0].bottom, JSON.stringify(row));
      above = Math.max(...row.map(cell => cell.bottom));
    }

    // Phone: one column; the part heads stack, then each generator's parts in order.
    await page.setViewportSize({ width: 390, height: 900 });
    await waitForColumns(1);
    const narrow = await layout();
    const stack = [...narrow.heads, narrow.makers[0], ...narrow.rows[0], narrow.makers[1], ...narrow.rows[1]];
    for (let index = 1; index < stack.length; index++) {
      assert.ok(stack[index].top >= stack[index - 1].bottom, `stacked item ${index} sits below item ${index - 1}`);
    }
    await page.setViewportSize({ width: 1400, height: 1000 });
    await waitForColumns(3);

    // The viewer walks generator A's parts in order, then generator B's.
    const viewerShows = (label, part) => page.waitForFunction(text =>
      el('image-viewer-generator').textContent === text, `${label} · ${part}`);
    const viewerItem = () => page.evaluate(() =>
      `${imageViewerState.jobId} ${imageViewerState.generator} ${imageViewerState.imageIndex}`);
    await page.locator(`#job-splitfx2 .cell[data-gen="${keyA}"] a[data-viewer-image="true"]`).click();
    await viewerShows(labelA, 'part 2 of 3 · Panels 5 to 8');
    await page.keyboard.press('ArrowRight');
    await viewerShows(labelA, 'part 3 of 3 · Panels 9 to 12');
    await page.keyboard.press('ArrowRight');
    await viewerShows(labelB, 'part 1 of 3 · Panels 1 to 4');
    await page.keyboard.press('ArrowLeft');
    await viewerShows(labelA, 'part 3 of 3 · Panels 9 to 12');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowLeft');
    await viewerShows(labelA, 'part 1 of 3 · Panels 1 to 4');
    assert.equal(await viewerItem(), `splitfx1 ${keyA} 1`);
    // Control+arrow moves to the first image of the same generator's next or previous part.
    await page.keyboard.press('Control+ArrowRight');
    await viewerShows(labelA, 'part 2 of 3 · Panels 5 to 8');
    await page.keyboard.press('Control+ArrowLeft');
    await viewerShows(labelA, 'part 1 of 3 · Panels 1 to 4');
    assert.equal(await viewerItem(), `splitfx1 ${keyA} 0`);
    await page.keyboard.press('Escape');

    // A hidden part leaves its column empty; the viewer skips it.
    await page.evaluate(() => el('job-splitfx2').classList.add('night-hidden'));
    const filtered = await layout();
    for (const [index, row] of filtered.rows.entries()) {
      assert.equal(row[0].left, wide.rows[index][0].left);
      assert.equal(row[2].left, wide.rows[index][2].left);
      assert.equal(row[2].top, row[0].top);
    }
    assert.deepEqual(await page.evaluate(() => getImageViewerPrompts()
      .filter(prompt => prompt.jobId.startsWith('splitfx'))
      .map(prompt => `${prompt.jobId} ${prompt.items.map(item => `${item.generator}#${item.imageIndex}`).join(',')}`)),
    [`splitfx1 ${keyA}#0,${keyA}#1`, `splitfx3 ${keyA}#0`, `splitfx1 ${keyB}#0`, `splitfx3 ${keyB}#0`]);
    await page.evaluate(() => el('job-splitfx2').classList.remove('night-hidden'));

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
    console.log('PASS: split dialog text persistence and import, failure, one-image, cancel, retry, generator rows, ' +
      'column layout at three widths, hidden parts, and viewer order.');
  } finally {
    for (const route of state.held) await route.abort().catch(() => {});
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
