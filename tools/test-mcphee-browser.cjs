const { chromium } = require('playwright');
const assert = require('node:assert/strict');
(async () => {
 const browser = await chromium.launch({headless:true});
 try {
  const page = await browser.newPage({viewport:{width:1400,height:1000}});
  const errors=[]; page.on('pageerror',e=>errors.push(e.message));
  const requests=[]; page.on('request',r=>{if(r.url().includes('mcphee/'))requests.push(new URL(r.url()).pathname);});
  await page.goto(process.env.MIC_UI_BASE_URL || 'http://127.0.0.1:5960/');
  await page.waitForFunction(()=>typeof mcphee !== 'undefined' && mcphee && !document.querySelector('#mcphee-enabled-toggle').disabled);
  assert.equal(await page.evaluate(()=>McPhee.version),'3.11.2');
  assert.ok(requests.some(p=>p.endsWith('en_US_2026.aff')));
  assert.ok(requests.some(p=>p.endsWith('en_US_2026.dic')));
  const prompt=page.locator('#prompt');
  await prompt.fill('teh online cat.');
  await prompt.press('End');
  await page.waitForFunction(()=>document.querySelector('.mcphee-backdrop .mcphee-mark-misspelled')?.textContent==='teh' && getComputedStyle(document.querySelector('.mcphee-backdrop')).visibility==='visible');
  assert.equal(await page.evaluate(()=>getComputedStyle(document.querySelector('.mcphee-backdrop')).visibility),'visible');
  await prompt.press('Control');
  assert.equal(await prompt.inputValue(),'the online cat.');
  await prompt.press('Control+z');
  assert.equal(await prompt.inputValue(),'teh online cat.');
  await page.locator('#mcphee-panel-toggle').click();
  await page.locator('.mcphee-formality-config').click();
  const original=page.locator('.mcphee-checker').filter({hasText:'misspellings (pink)'});
  await original.locator('input[type=checkbox]').check();
  await page.waitForFunction(()=>JSON.parse(localStorage.getItem(MultiImagePersonalConfiguration.StorageKey)).spelling.ruleOverrides.checkers.spell.enabled===true);
  const echo=page.locator('.mcphee-checker').filter({hasText:'same word or phrase nearby'});
  await echo.locator('.mcphee-checker-order').fill('12');
  await echo.locator('.mcphee-checker-order').press('Tab');
  await page.waitForFunction(()=>JSON.parse(localStorage.getItem(MultiImagePersonalConfiguration.StorageKey)).spelling.ruleOverrides.checkers.echo.order===12);
  await echo.locator('.mcphee-checker-param input').first().fill('7');
  await echo.locator('.mcphee-checker-param input').first().press('Tab');
  await page.waitForFunction(()=>JSON.parse(localStorage.getItem(MultiImagePersonalConfiguration.StorageKey)).spelling.ruleOverrides.checkers.echo.params.echoWindowWords===7);
  assert.ok(await page.evaluate(()=>buildPersonalConfiguration().spelling.ruleOverrides.checkers.spell.enabled));
  await page.reload();
  await page.waitForFunction(()=>typeof mcphee !== 'undefined' && mcphee && !document.querySelector('#mcphee-enabled-toggle').disabled);
  const saved=await page.evaluate(()=>buildPersonalConfiguration().spelling.ruleOverrides.checkers);
  assert.equal(saved.spell.enabled,true); assert.equal(saved.echo.order,12);assert.equal(saved.echo.params.echoWindowWords,7);
  await prompt.fill('teh online cat.'); await prompt.press('End');
  await page.locator('#mcphee-panel-toggle').click();
  await page.screenshot({path:'/tmp/mic-mcphee-composer.png'});
  // A font attached to McPhee's class must survive forced style mirroring.
  await page.addStyleTag({content:'.mcphee-textarea { font: 22px/1.4 serif !important; }'});
  await page.evaluate(()=>mcpheeCtl.refresh(true));
  assert.deepEqual(await page.evaluate(()=>{
   const ta=getComputedStyle(document.querySelector('#prompt'));
   const bd=getComputedStyle(document.querySelector('.mcphee-backdrop'));
   return [bd.font===ta.font,bd.visibility];
  }),[true,'visible']);
  assert.deepEqual(errors,[]);
  console.log('PASS: real composer loads 3.11.2, fetches 2026 dictionary, highlights, corrects, undoes, and persists checker options across reload.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
