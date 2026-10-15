// Composer characters, end to end against a local auth-off test server.
// It creates and deletes characters there. It never presses the paid
// "write it from my prompts" button and submits no image job.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname), 'Use a local test server.');

const biscuit = 'a small cream Shiba Inu with a red collar and a curled tail';
const zuzu = 'a cheerful girl of about eight with two puffy black braids and a yellow raincoat';

async function addCharacter(page, name, kind, description) {
  await page.locator('#character-new').click();
  await page.locator('#character-name').fill(name);
  await page.locator('#character-kind').selectOption(kind);
  await page.locator('#character-description').fill(description);
  await page.locator('#character-save').click();
  await page.waitForFunction((n) => document.querySelector('#character-status').textContent === `Saved ${n}.`, name);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
  page.on('dialog', (dialog) => dialog.accept());
  await page.goto(base);
  await page.waitForFunction(() => typeof mcphee !== 'undefined' && mcphee && !document.querySelector('#mcphee-enabled-toggle').disabled);
  await page.evaluate(() => { localStorage.clear(); });
  await page.reload();
  await page.waitForFunction(() => typeof mcphee !== 'undefined' && mcphee && !document.querySelector('#mcphee-enabled-toggle').disabled);

  await page.locator('#characters-toggle').click();
  for (const name of ['Biscuit', 'Zuzu', 'Rogue']) {
    const existing = await page.evaluate((n) => characters.find((c) => c.name === n)?.id || '', name);
    if (existing) {
      await page.evaluate((id) => openCharacterEditor(id), existing);
      await page.locator('#character-delete').click();
      await page.waitForFunction((n) => !characters.some((c) => c.name === n), name);
    }
  }
  await addCharacter(page, 'Biscuit', 'character', biscuit);
  await addCharacter(page, 'Zuzu', 'character', zuzu);

  // A second character with the same name is refused, never merged.
  await page.locator('#character-new').click();
  await page.locator('#character-name').fill('biscuit');
  await page.locator('#character-description').fill('another dog');
  await page.locator('#character-save').click();
  await page.waitForFunction(() => document.querySelector('#character-status').textContent.includes('already have a character named Biscuit'));
  await page.locator('#character-cancel').click();

  // Names are not misspellings; mentions show in the strip under the prompt.
  const prompt = page.locator('#prompt');
  await prompt.fill('Zuzu and Biscuit fly a kite on a bright sunny beach. Biscuit barks.');
  await page.waitForFunction(() => document.querySelectorAll('#character-strip .character-chip').length === 3, null, { timeout: 5000 })
    .catch(async (err) => { console.error(await page.evaluate(() => [promptBox.value, document.querySelector('#character-strip').outerHTML, characters.map((c) => c.name)])); throw err; });
  assert.deepEqual(await page.locator('#character-strip .character-chip').allTextContents(),
    ['describe Zuzu', 'describe Biscuit', 'describe all 2']);
  assert.equal(await page.evaluate(() => [...document.querySelectorAll('.mcphee-backdrop .mcphee-mark-misspelled')]
    .map((m) => m.textContent).filter((w) => /zuzu|biscuit/i.test(w)).length), 0);

  await page.locator('#character-strip .character-chip', { hasText: 'describe Biscuit' }).click();
  assert.equal(await prompt.inputValue(), `Zuzu and Biscuit (${biscuit}) fly a kite on a bright sunny beach. Biscuit barks.`);
  assert.deepEqual(await page.locator('#character-strip .character-chip').allTextContents(),
    ['describe Zuzu', 'Biscuit ✓', 'undo']);

  await page.locator('#character-strip .character-chip', { hasText: 'undo' }).click();
  assert.equal(await prompt.inputValue(), 'Zuzu and Biscuit fly a kite on a bright sunny beach. Biscuit barks.');

  await page.locator('#character-strip .character-chip', { hasText: 'describe all 2' }).click();
  assert.equal(await prompt.inputValue(),
    `Zuzu (${zuzu}) and Biscuit (${biscuit}) fly a kite on a bright sunny beach. Biscuit barks.`);

  // Undo disappears once the user edits the prompt again.
  await prompt.press('End');
  await prompt.type(' Wow.');
  assert.equal(await page.locator('#character-strip .character-chip', { hasText: 'undo' }).count(), 0);

  // Insert goes to the cursor and describes only an undescribed character.
  await prompt.fill('A picnic with  in the park.');
  await prompt.evaluate((box) => box.setSelectionRange(14, 14));
  await page.locator('.character-row', { hasText: 'Zuzu' }).getByRole('button', { name: 'insert' }).click();
  assert.equal(await prompt.inputValue(), `A picnic with Zuzu (${zuzu}) in the park.`);

  // Find lists matching past prompts without any model call.
  await addCharacter(page, 'Rogue', 'object', 'a playing card showing a masked figure with a curved dagger');
  await page.locator('#character-find').click();
  await page.waitForFunction(() => !document.querySelector('#character-matches').hidden);
  assert.match(await page.locator('#character-matches-summary').textContent(), /of your prompts? mentions? Rogue/);

  await page.screenshot({ path: '/tmp/mic-characters-panel.png', fullPage: false });
  await page.locator('#compose').screenshot({ path: '/tmp/mic-characters-composer.png' }).catch(() => {});

  // Rename keeps the same record; delete removes it.
  await page.locator('#character-name').fill('Rogue Card');
  await page.locator('#character-save').click();
  await page.waitForFunction(() => document.querySelector('#character-status').textContent === 'Saved Rogue Card.');
  assert.equal(await page.evaluate(() => characters.filter((c) => c.name.startsWith('Rogue')).length), 1);
  await page.locator('#character-delete').click();
  await page.waitForFunction(() => !characters.some((c) => c.name.startsWith('Rogue')));

  for (const name of ['Biscuit', 'Zuzu']) {
    const id = await page.evaluate((n) => characters.find((c) => c.name === n).id, name);
    await page.evaluate((i) => openCharacterEditor(i), id);
    await page.locator('#character-delete').click();
    await page.waitForFunction((n) => !characters.some((c) => c.name === n), name);
  }
  await prompt.fill('');
  await browser.close();
  console.log('characters browser test passed');
})().catch((err) => { console.error(err); process.exit(1); });
