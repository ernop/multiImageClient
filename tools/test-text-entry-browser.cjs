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

      await page.setViewportSize({ width: 390, height: 640 });
      await prompt.fill('A wrapping prompt with café, emoji 🐈, and tabs\t'.repeat(40) + '\n\n');
      await typeAtBottom(prompt);
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
      console.log(`PASS ${name}: Enter, wrapping, paste, spare lines, page/panel scrolling, undo, drafts, resize, spelling, dynamic and hidden fields.`);
    } finally {
      await browser.close();
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
