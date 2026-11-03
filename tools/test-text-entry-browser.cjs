const { chromium, firefox } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

// Measure the caret line independently with a native textarea containing its prefix.
// These checks use line ends, including empty lines, rather than a DOM caret mirror.
async function geometry(field) {
  return field.evaluate(field => {
    const style = getComputedStyle(field);
    const probe = document.createElement('textarea');
    probe.readOnly = true;
    for (const name of style) probe.style.setProperty(name, style.getPropertyValue(name));
    Object.assign(probe.style, { position: 'fixed', top: '0', left: '-10000px',
      width: field.getBoundingClientRect().width + 'px', height: '0', minHeight: '0',
      maxHeight: 'none', flex: 'none', overflow: 'hidden', visibility: 'hidden' });
    probe.value = field.value.slice(0, field.selectionEnd);
    document.body.append(probe);
    const rect = field.getBoundingClientRect();
    const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
    const bottom = rect.top + parseFloat(style.borderTopWidth) + probe.scrollHeight
      - parseFloat(style.paddingBottom) - field.scrollTop;
    probe.remove();
    let visibleTop = 0, visibleBottom = innerHeight;
    for (let parent = field.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
      if (/auto|scroll|hidden/.test(getComputedStyle(parent).overflowY)) {
        const parentRect = parent.getBoundingClientRect();
        visibleTop = Math.max(visibleTop, parentRect.top + parent.clientTop);
        visibleBottom = Math.min(visibleBottom, parentRect.top + parent.clientTop + parent.clientHeight);
      }
    }
    // The phone Generate row, or any other bar stuck to the bottom edge, hides the text behind it.
    for (const bar of document.querySelectorAll('#send-row, [data-text-entry-inset="bottom"]')) {
      const position = getComputedStyle(bar).position;
      const barRect = bar.getBoundingClientRect();
      if ((position === 'sticky' || position === 'fixed') && barRect.top < innerHeight && barRect.bottom >= innerHeight - 1) {
        visibleBottom = Math.min(visibleBottom, barRect.top);
      }
    }
    return { bottom, visibleTop, visibleBottom, lineHeight, height: rect.height,
      spare: rect.bottom - bottom, scrollTop: field.scrollTop,
      clientHeight: field.clientHeight, scrollHeight: field.scrollHeight, pageScroll: scrollY };
  });
}

async function visible(field) {
  // Allow the browser's own post-key scrolling and ResizeObserver to settle.
  await field.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const g = await geometry(field);
  assert.ok(g.scrollHeight <= g.clientHeight + 1, 'Text must fit without an inner scrollbar: ' + JSON.stringify(g));
  assert.equal(g.scrollTop, 0, 'Keep the textarea at its top after growing.');
  assert.ok(g.spare >= g.lineHeight * 1.7, 'Reserve two lines below text: ' + JSON.stringify(g));
  assert.ok(g.bottom <= g.visibleBottom - g.lineHeight, 'Keep typing above the bottom edge: ' + JSON.stringify(g));
  assert.ok(g.bottom - g.lineHeight >= g.visibleTop - 2, 'Keep the caret line visible: ' + JSON.stringify(g));
  return g;
}

// Puts the caret at the start of a tall field, scrolls the page to a lower part
// of it, and clicks or taps a visible line. The page must stay where it was and
// the caret must land on the clicked line. Waits through the spelling overlay's
// re-render and its 700 ms poll, which re-checks the overlay.
async function pointerStays(page, field, label, { focused = false, below = null, nearBottom = false, tap = false }) {
  const frames = () => field.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await field.evaluate((field, focused) => {
    field.setSelectionRange(0, 0);
    if (focused) field.focus({ preventScroll: true }); else field.blur();
  }, focused);
  // Focusing reveals the caret; the user then scrolls away from it.
  await frames();
  const aim = await field.evaluate((field, { below, nearBottom }) => {
    const rect = field.getBoundingClientRect();
    const top = rect.top + scrollY;
    const wanted = Math.round(below == null ? top + rect.height * 0.7 - innerHeight / 2 : top + rect.height - innerHeight + below);
    scrollTo(0, wanted);
    const now = field.getBoundingClientRect();
    const y = below != null ? now.bottom - 150 : nearBottom ? innerHeight - 10 : innerHeight / 2;
    return { wanted, x: now.left + now.width / 2, y };
  }, { below, nearBottom });
  await frames();
  const before = await page.evaluate(() => Math.round(scrollY));
  assert.equal(before, aim.wanted, `The page scrolls to ${label}.`);
  if (tap) await page.touchscreen.tap(aim.x, aim.y);
  else await page.mouse.click(aim.x, aim.y);
  await page.waitForTimeout(1000);
  const g = await geometry(field);
  assert.equal(Math.round(g.pageScroll), before, `Clicking ${label} must not scroll the page: ` + JSON.stringify(g));
  assert.ok(aim.y > g.bottom - g.lineHeight - 2 && aim.y < g.bottom + 2,
    `The caret lands on the clicked line in ${label}: ` + JSON.stringify({ aim, g }));
}

async function longPrompt(prompt) {
  const paragraph = 'The bright market square fills with clear morning light, and the vendors arrange their fruit in tidy rows while children chase pigeons across the warm stones.';
  await prompt.fill(Array.from({ length: 40 }, (_, i) => `${i + 1}. ${paragraph}`).join('\n'));
  await prompt.page().waitForTimeout(900);
}

// Replacing the whole prompt, as an AI rewrite does, fits the field to the new text.
// The rewrite request receives a fixed local reply, so no provider sees it.
async function replacementFits(page, prompt) {
  const frames = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const layout = () => page.evaluate(() => {
    const rect = document.getElementById('prompt').getBoundingClientRect();
    return { y: Math.round(scrollY), top: rect.top, bottom: rect.bottom, height: rect.height };
  });
  const paragraph = 'The bright harbor fills with clear noon light, and the sailboats rest in tidy rows while gulls circle above the white pier.';
  const paragraphs = count => Array.from({ length: count }, (_, i) => `${i + 1}. ${paragraph}`).join('\n');
  const short = paragraphs(2), medium = paragraphs(15), long = paragraphs(45);
  const typedHeight = async text => {
    await prompt.fill('');
    await prompt.fill(text);
    await frames();
    return (await layout()).height;
  };
  const shortHeight = await typedHeight(short);
  const mediumHeight = await typedHeight(medium);
  await prompt.fill(long);
  await frames();
  const tall = await layout();
  assert.ok(tall.height > mediumHeight + 600, 'The long prompt stretches the field: ' + JSON.stringify({ tall, mediumHeight }));

  // The view shows the end of the prompt and the buttons under it; its top is far above.
  await page.evaluate(() => {
    const tools = document.getElementById('prompt-tools').getBoundingClientRect();
    scrollTo(0, scrollY + tools.bottom - innerHeight + 60);
  });
  await frames();
  const before = await layout();
  assert.ok(before.top < 0, 'The prompt starts above the view: ' + JSON.stringify(before));
  await page.route('**/api/prompt/advice', route => route.fulfill({ json: { replacement: medium, exchangeId: 'text-entry-test' } }));
  const button = await page.locator('[data-rewrite-model="claude-sonnet-5-5"]').boundingBox();
  await page.mouse.click(button.x + button.width / 2, button.y + button.height / 2);
  await page.waitForFunction(() => document.getElementById('prompt-rewrite-status').textContent === 'Expanded prompt applied.');
  await page.unroute('**/api/prompt/advice');
  await frames();
  await page.waitForTimeout(900);
  const rewritten = await layout();
  assert.equal(await prompt.inputValue(), medium);
  assert.ok(Math.abs(rewritten.height - mediumHeight) <= 1,
    'A shorter rewrite is as tall as the same text typed into an empty prompt: ' + JSON.stringify({ mediumHeight, rewritten }));
  assert.ok(Math.abs(rewritten.bottom - before.bottom) <= 1,
    'The bottom edge and the buttons under it keep their place: ' + JSON.stringify({ before, rewritten }));
  assert.equal(await page.evaluate(() => getComputedStyle(document.querySelector('.mcphee-backdrop')).visibility), 'visible',
    'The spelling overlay still matches the fitted field.');

  // With the prompt's top in view, the page stays still while the field grows or shrinks.
  await page.evaluate(() => scrollTo(0, 0));
  await frames();
  const atTop = await layout();
  await page.evaluate(text => applyViewedPromptToComposer(text), long);
  await frames();
  const grown = await layout();
  assert.ok(grown.y === atTop.y && Math.abs(grown.top - atTop.top) <= 1 && grown.height > atTop.height + 600,
    'A longer replacement grows the field downward: ' + JSON.stringify({ atTop, grown }));
  await page.evaluate(text => applyViewedPromptToComposer(text), short);
  await frames();
  const shrunk = await layout();
  assert.ok(shrunk.y === atTop.y && Math.abs(shrunk.height - shortHeight) <= 1,
    'A shorter replacement fits the field to its text: ' + JSON.stringify({ shortHeight, shrunk }));

  // Typing never shrinks the field, and a height the user dragged survives a replacement.
  await prompt.fill(long);
  await frames();
  const typedOver = (await layout()).height;
  await prompt.press('Control+a');
  await prompt.press('x');
  await frames();
  assert.ok(Math.abs((await layout()).height - typedOver) <= 1, 'Typing over a long prompt keeps its height.');
  const dragged = await prompt.evaluate(field => {
    field.style.height = parseFloat(getComputedStyle(field).height) + 200 + 'px';
    return field.getBoundingClientRect().height;
  });
  await page.evaluate(text => applyViewedPromptToComposer(text), short);
  await frames();
  assert.ok(Math.abs((await layout()).height - dragged) <= 1, 'A replacement keeps a height the user dragged.');
  await prompt.fill('');
}

async function typeAtBottom(field) {
  await field.focus();
  await field.press('Control+End');
  for (let i = 0; i < 4; i++) {
    await field.press('Enter');
    await visible(field);
    await field.pressSequentially('bright café 🐈');
    await visible(field);
  }
}

(async () => {
  for (const [name, engine] of Object.entries({ chromium, firefox })) {
    const browser = await engine.launch({ headless: true });
    try {
      const context = await browser.newContext({ viewport: { width: 1280, height: 720 } });
      await context.addInitScript(() => {
        window.textEntryOverflow = [];
        document.addEventListener('input', event => {
          const field = event.target;
          if (field.tagName === 'TEXTAREA' && !field.readOnly && field.clientWidth
              && field.scrollHeight > field.clientHeight + 1) {
            window.textEntryOverflow.push(field.id);
          }
        });
      });
      // Never submit jobs, change server preferences, or contact paid providers.
      await context.route('**/*', route => route.request().method() === 'GET' ? route.continue() : route.abort());
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(base);
      await page.waitForFunction(() => typeof mcpheeCtl !== 'undefined' && mcpheeCtl);
      const prompt = page.locator('#prompt');
      await prompt.fill(Array.from({ length: 35 }, (_, i) => `Line ${i + 1}: bright daylight.`).join('\n'));
      await typeAtBottom(prompt);
      const grown = await visible(prompt);
      assert.ok(grown.height > 720);
      assert.ok(grown.pageScroll > 0);
      await page.waitForFunction(() => getComputedStyle(document.querySelector('.mcphee-backdrop')).visibility === 'visible');
      const height = await prompt.evaluate(field => field.getBoundingClientRect().height);
      await page.waitForTimeout(150);
      assert.equal(await prompt.evaluate(field => field.getBoundingClientRect().height), height, 'Flex sizing must not keep growing.');

      // Editing earlier text must follow that caret, not force the document to its end.
      await prompt.press('Control+Home');
      await prompt.press('End');
      await prompt.press('Enter');
      await visible(prompt);
      await prompt.press('x');
      const withEdit = await prompt.inputValue();
      await prompt.press('Control+z');
      assert.notEqual(await prompt.inputValue(), withEdit, 'Sizing preserves native undo.');
      // Browsers group Enter and the following character differently.
      await prompt.press('Control+Shift+z');
      assert.equal(await prompt.inputValue(), withEdit, 'Redo restores the exact edit.');

      // A pointer places the caret where the user is looking. Chromium focused the field
      // first and revealed the old caret; the overlay's check clamped Firefox's page.
      await longPrompt(prompt);
      await pointerStays(page, prompt, 'the lower part of the prompt', {});
      await pointerStays(page, prompt, 'the prompt with the view past its bottom edge', { below: 250 });
      await pointerStays(page, prompt, 'a focused prompt next to the bottom of the view', { focused: true, nearBottom: true });
      await prompt.press('ArrowDown');
      await visible(prompt);
      await replacementFits(page, prompt);

      await page.setViewportSize({ width: 390, height: 640 });
      await prompt.fill('A wrapping prompt with café, emoji 🐈, and tabs\t'.repeat(40) + '\n\n');
      await typeAtBottom(prompt);
      const sendRow = await page.locator('#send-row').evaluate(row => ({
        position: getComputedStyle(row).position, bottom: row.getBoundingClientRect().bottom, innerHeight }));
      assert.equal(sendRow.position, 'sticky');
      assert.ok(Math.abs(sendRow.bottom - sendRow.innerHeight) <= 1,
        'The phone Generate row must cover the bottom edge while typing: ' + JSON.stringify(sendRow));
      await page.screenshot({ path: `/tmp/mic-text-entry-${name}.png` });
      await page.reload();
      await prompt.focus();
      await prompt.press('Control+End');
      await visible(prompt);
      await prompt.fill('');
      assert.ok(await prompt.evaluate(field => field.getBoundingClientRect().height) < 700, 'Clearing resets automatic growth.');
      await page.setViewportSize({ width: 1280, height: 720 });
      for (const [dialogId, fieldId] of [['claude-advice-dialog', 'claude-advice-instruction'], ['video-dialog', 'video-prompt']]) {
        await page.evaluate(id => document.getElementById(id).showModal(), dialogId);
        const field = page.locator('#' + fieldId);
        await field.fill('A bright garden with clear labels.\n'.repeat(25));
        await typeAtBottom(field);
        await page.evaluate(id => document.getElementById(id).close(), dialogId);
      }
      assert.deepEqual(await page.evaluate(() => textEntryOverflow), [], 'Growth must finish during the input event.');

      // The same behavior must work independently of McPhee.
      await page.goto(new URL('goal.html', base).href);
      await page.setViewportSize({ width: 1280, height: 720 });
      const goal = page.locator('#goal-text');
      await goal.fill('A bright garden with clear labels.\n'.repeat(35));
      await typeAtBottom(goal);
      assert.ok(await page.locator('#goal-sidebar').evaluate(sidebar => sidebar.scrollTop) > 0);

      // Dynamic fork/configuration editors and initially hidden dialogs use the same module.
      await page.evaluate(() => {
        const panel = document.createElement('div');
        panel.id = 'text-entry-dialog-test';
        panel.style.cssText = 'position:fixed;top:100px;left:20px;width:350px;max-height:380px;overflow:auto;background:white;z-index:10000;display:none';
        panel.innerHTML = '<div style="height:160px"></div><div class="goal-editor"><textarea id="dynamic-prompt" rows="4"></textarea></div><div style="height:70px"></div>';
        document.body.append(panel);
        document.getElementById('dynamic-prompt').value = 'A dynamic prompt.\n'.repeat(20);
      });
      await page.evaluate(() => { document.getElementById('text-entry-dialog-test').style.display = 'block'; });
      const dynamic = page.locator('#dynamic-prompt');
      await typeAtBottom(dynamic);
      assert.ok(await page.locator('#text-entry-dialog-test').evaluate(panel => panel.scrollTop) > 0);
      await page.locator('#text-entry-dialog-test').evaluate(panel => panel.remove());
      assert.deepEqual(await page.evaluate(() => textEntryOverflow), [], 'Growth must finish during the input event.');
      assert.deepEqual(errors, []);

      // A touch tap places the caret in compatibility mouse events after pointerup.
      const touch = await browser.newContext({ viewport: { width: 390, height: 640 }, hasTouch: true });
      await touch.route('**/*', route => route.request().method() === 'GET' ? route.continue() : route.abort());
      const phone = await touch.newPage();
      phone.on('pageerror', error => errors.push(error.message));
      await phone.goto(base);
      await phone.waitForFunction(() => typeof mcpheeCtl !== 'undefined' && mcpheeCtl);
      await longPrompt(phone.locator('#prompt'));
      await pointerStays(phone, phone.locator('#prompt'), 'a tapped phone prompt', { tap: true });
      await touch.close();
      assert.deepEqual(errors, []);
      console.log(`PASS ${name}: Enter, wrapping, paste, spare lines, page/panel scrolling, sticky bottom bar, undo, drafts, resize, spelling, dynamic and hidden fields, clicks and taps that keep the page still, replacements that fit the text.`);
    } finally {
      await browser.close();
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
