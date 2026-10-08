const { chromium, firefox, devices } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

const longPrompt = Array.from({ length: 40 }, (_, i) =>
  `Scene ${i + 1}: a bright daytime market stall with lemons, striped awnings, and clear signs.`).join(' ');
const shortPrompt = 'A red apple on a white plate in bright daylight.';

function device(name) {
  const { defaultBrowserType, ...options } = devices[name];
  return options;
}

const runs = [
  { name: 'Pixel 7', engine: chromium, options: device('Pixel 7'), phone: true, portrait: true },
  { name: 'Galaxy S8', engine: chromium, options: device('Galaxy S8'), phone: true, portrait: true },
  { name: 'Pixel 7 landscape', engine: chromium, options: device('Pixel 7 landscape'), phone: true, portrait: false },
  // Playwright cannot emulate a mobile Firefox; the width alone selects the phone rules.
  { name: 'Firefox 390 px', engine: firefox, options: { viewport: { width: 390, height: 740 } }, phone: true, portrait: true },
  { name: 'desktop', engine: chromium, options: { viewport: { width: 1400, height: 900 } }, phone: false },
];

async function promptBox(card) {
  return card.locator('.job-prompt').evaluate(prompt => {
    const rect = prompt.getBoundingClientRect();
    return {
      top: rect.top,
      lines: rect.height / parseFloat(getComputedStyle(prompt).lineHeight),
      text: prompt.textContent,
      headerBottom: document.querySelector('body > header').getBoundingClientRect().bottom,
      innerHeight,
    };
  });
}

async function toggleState(card) {
  return card.locator('.job-prompt-toggle').evaluate(toggle => ({
    hidden: toggle.hidden, text: toggle.textContent, expanded: toggle.getAttribute('aria-expanded'),
    bottom: toggle.getBoundingClientRect().bottom,
  }));
}

(async () => {
  for (const run of runs) {
    const browser = await run.engine.launch({ headless: true });
    try {
      const context = await browser.newContext(run.options);
      // Never submit jobs, change server preferences, or contact paid providers.
      await context.route('**/*', route => route.request().method() === 'GET' ? route.continue() : route.abort());
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(base);
      await page.waitForFunction(() => document.querySelectorAll('#gens-row .gen-toggle').length > 0);

      const layout = await page.evaluate(() => ({
        phone: PhoneLayoutQuery.matches,
        header: document.querySelector('body > header').getBoundingClientRect().height,
        sendRow: getComputedStyle(document.querySelector('#send-row')).position,
        composeTop: getComputedStyle(document.querySelector('#compose-top')).flexDirection,
        promptWidth: document.querySelector('#prompt').getBoundingClientRect().width,
        innerWidth,
      }));
      assert.equal(layout.phone, run.phone, `${run.name}: phone layout query`);
      assert.equal(layout.sendRow === 'sticky', layout.phone, 'app.js PhoneLayoutQuery must match the CSS phone query.');

      await page.evaluate(({ longPrompt, shortPrompt }) => {
        addJobCard('phone-layout-test-short', shortPrompt, ['gpt2'], false, Date.now(), 0, {});
        addJobCard('phone-layout-test-long', longPrompt, ['gpt2'], false, Date.now(), 0, {});
      }, { longPrompt, shortPrompt });
      const long = page.locator('#job-phone-layout-test-long');
      const short = page.locator('#job-phone-layout-test-short');
      // The toggle state follows a resize observation, one frame after layout.
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

      if (!run.phone) {
        assert.ok((await promptBox(long)).lines > 6.5, 'Desktop shows the whole prompt.');
        assert.equal((await toggleState(long)).hidden, true, 'Desktop has no prompt toggle.');
        assert.deepEqual(errors, []);
        console.log(`PASS ${run.name}: static Generate row, unclamped job prompt, no prompt toggle.`);
        continue;
      }

      assert.equal(layout.header, 46, 'The phone header is one 46 px row.');
      if (run.portrait) {
        assert.equal(layout.composeTop, 'column', 'Portrait stacks the paste zone above the prompt.');
        assert.ok(layout.promptWidth >= layout.innerWidth * 0.8, `The portrait prompt uses the screen width: ${JSON.stringify(layout)}`);
      } else {
        assert.equal(layout.composeTop, 'row', 'Landscape keeps the paste zone beside the prompt.');
      }

      await page.waitForFunction(() => !document.querySelector('#job-phone-layout-test-long .job-prompt-toggle').hidden);
      const clamped = await promptBox(long);
      assert.ok(clamped.lines <= 6.05, `A long job prompt shows at most six lines: ${clamped.lines}`);
      assert.equal(clamped.text, longPrompt, 'The clamp leaves the prompt text unchanged.');
      assert.deepEqual(await toggleState(long).then(({ hidden, text, expanded }) => ({ hidden, text, expanded })),
        { hidden: false, text: 'show full prompt', expanded: 'false' });
      assert.equal((await toggleState(short)).hidden, true, 'A prompt that fits has no toggle.');

      await long.locator('.job-prompt-toggle').click();
      const expanded = await promptBox(long);
      assert.ok(expanded.lines > 6.5, `Expanding shows the whole prompt: ${expanded.lines}`);
      assert.deepEqual(await toggleState(long).then(({ hidden, text, expanded }) => ({ hidden, text, expanded })),
        { hidden: false, text: 'show less', expanded: 'true' });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'No row is wider than the screen.');

      // Collapse from the end of the expanded prompt, where its start is above the screen.
      // Chromium and Firefox keep the toggle in place through scroll anchoring. Safari has
      // no scroll anchoring, so the second pass turns it off to exercise app.js.
      for (const anchoring of [true, false]) {
        if (!anchoring) {
          await page.evaluate(() => {
            document.documentElement.style.overflowAnchor = 'none';
            document.body.style.overflowAnchor = 'none';
          });
          await long.locator('.job-prompt-toggle').click();
        }
        await long.locator('.job-prompt-toggle').evaluate(toggle => toggle.scrollIntoView({ block: 'end' }));
        assert.ok((await promptBox(long)).top < expanded.headerBottom, 'The expanded prompt starts above the screen.');
        await long.locator('.job-prompt-toggle').click();
        const collapsed = await promptBox(long);
        const after = await toggleState(long);
        assert.ok(collapsed.lines <= 6.05);
        assert.ok(collapsed.top >= collapsed.headerBottom - 1 && after.bottom <= collapsed.innerHeight + 1,
          `Collapsing keeps the prompt and its toggle on screen: ${JSON.stringify({ anchoring, top: collapsed.top, headerBottom: collapsed.headerBottom, after })}`);
        assert.equal(after.text, 'show full prompt');
      }

      assert.deepEqual(errors, []);
      console.log(`PASS ${run.name}: ${run.portrait ? 'stacked' : 'side-by-side'} composer, sticky Generate, 46 px header, no sideways overflow, six-line job prompt with toggle.`);
    } finally {
      await browser.close();
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
