const { chromium } = require('playwright');
const fs = require('fs');
const assert = require('assert/strict');
const root = require('path').resolve(__dirname, '../MultiImageClient/Ui/wwwroot');
(async () => {
 const browser = await chromium.launch({headless:true});
 const page = await browser.newPage({viewport:{width:1280,height:1000}});
 let records = [], calls = [], hold = null, fail = false;
 const fixture = fs.readFileSync(root+'/index.html','utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '') + `
 <script src="prompt-rewrites.js"></script><script>
 const prompt = document.getElementById('prompt');
 document.getElementById('username-input').value = 'Alice';
 prompt.value = '  A botanical library inside a greenhouse.\\nKeep the books dry.  ';
 window.controls = createPromptRewrites({apiUrl:p=>p, promptBox:prompt,
 username:()=>document.getElementById('username-input').value,
 applyPrompt:t=>{prompt.value=t;prompt.dispatchEvent(new Event('input'))}});
 controls.configure([{model:'claude-fable-5-1',available:true},{model:'gpt-6-astra',available:true}]);
 </script>`;
 await page.route('https://rewrite.test/**', async route=> {
  const path = new URL(route.request().url()).pathname;
  if(path === '/') return route.fulfill({contentType:'text/html',body:fixture});
  if(path === '/api/prompt/advice/history') {
   const q = new URL(route.request().url()).searchParams;
   const offset = q.get('beforeId') ? records.findIndex(x=>x.id===q.get('beforeId'))+1 : 0;
   return route.fulfill({json:{exchanges:records.slice(offset,offset+20)}});
  }
  if(path === '/api/prompt/advice') {
   const body=route.request().postData();
   const field=name=>new URLSearchParams(body).get(name);
   const model=field('model'), original=field('prompt'); calls.push(model);
   if (hold) await hold;
   if(fail) return route.fulfill({status:502,json:{error:'Provider refused the rewrite.'}});
   const source=records.find(x=>x.id===field('exchangeId'));
   const result=model==='restore' ? (field('side')==='original' ? source.originalPrompt:source.resultPrompt)
       : 'A sunlit botanical library fills a tall glass greenhouse.\n\nWarm oak shelves stand beneath a sealed glass canopy. Keep every book dry. '+model;
   const id=String(records.length+1);
   records.unshift({id, model, originalPrompt:original, resultPrompt:result, status:'succeeded',requestedAtUnixMs:Date.now(), instruction:'Expand the intent.',rawResponse:'wire'});
   return route.fulfill({json:{replacement:result,model,originalPrompt:original,exchangeId:id}});
  }
  const file=root+path;
  if(fs.existsSync(file) && fs.statSync(file).isFile()) return route.fulfill({path:file});
  return route.fulfill({status:404,body:''});
 });
 await page.goto('https://rewrite.test/');
 await page.getByRole('button',{name:'flesh out · Fable 5.1',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('prompt-rewrite-status').textContent.startsWith('Expanded prompt applied'));
 assert((await page.locator('#prompt').inputValue()).includes('claude-fable-5-1'));
 assert.equal(await page.locator('.prompt-rewrite-exchange pre').first().textContent(),'  A botanical library inside a greenhouse.\nKeep the books dry.  ');
 assert(await page.locator('#prompt').evaluate(e=>e.classList.contains('prompt-not-submitted')));
 assert.equal(await page.locator('section.prompt-not-submitted').count(),1);
 await page.evaluate(()=>controls.submitted(document.getElementById('prompt').value.trim(), controls.revision));
 assert(!(await page.locator('#prompt').evaluate(e=>e.classList.contains('prompt-not-submitted'))));
 assert.equal(await page.locator('section.prompt-not-submitted').count(),0);
 const acceptedRevision = await page.evaluate(()=>controls.revision);
 await page.locator('#prompt').fill('A newer draft');
 await page.evaluate(r=>controls.submitted('A newer draft',r),acceptedRevision);
 assert(await page.locator('#prompt').evaluate(e=>e.classList.contains('prompt-not-submitted')));
 await page.getByRole('button',{name:'flesh out · GPT-6',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('.prompt-rewrite-exchange').length===2);
 await page.locator('.prompt-rewrite-exchange').last().getByRole('button',{name:'restore this version'}).first().click();
 await page.waitForFunction(()=>document.querySelectorAll('.prompt-rewrite-exchange').length===3);
 assert((await page.locator('#prompt').inputValue()).includes('A botanical library inside'));
 assert(records[0].originalPrompt.includes('gpt-6-astra'));
 let release; hold=new Promise(resolve=>release=resolve);
 await page.getByRole('button',{name:'flesh out · Fable 5.1',exact:true}).click();
 await page.locator('#prompt').fill('My edit during the request');
 release(); hold=null;
 await page.waitForFunction(()=>document.getElementById('prompt-rewrite-status').textContent.includes('changed during'));
 assert.equal(await page.locator('#prompt').inputValue(),'My edit during the request');
 fail=true;
 await page.getByRole('button',{name:'flesh out · GPT-6',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('prompt-rewrite-status').textContent.includes('refused'));
 assert.equal(await page.locator('#prompt').inputValue(),'My edit during the request');
 fail=false;
 for(let i=0;i<25;i++) records.push({...records[0],id:'old'+i});
 await page.getByRole('button',{name:'refresh history',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('.prompt-rewrite-exchange').length===20);
 await page.getByRole('button',{name:'older changes',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('.prompt-rewrite-exchange').length===9);
 await page.getByRole('button',{name:'newer changes',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('.prompt-rewrite-exchange').length===20);
 await page.setViewportSize({width:390,height:844});
 assert(await page.locator('#prompt-rewrite-history').evaluate(e=>e.getBoundingClientRect().right <= innerWidth));
 assert(await page.locator('#prompt-tools').evaluate(e=>e.getBoundingClientRect().right <= innerWidth));
 await page.screenshot({path:'/tmp/prompt-rewrite-mobile.png'});
 await page.setViewportSize({width:1280,height:1000});
 await page.locator('#prompt-rewrite-history').scrollIntoViewIfNeeded();
 await page.screenshot({path:'/tmp/prompt-rewrite-desktop.png'});
 await page.locator('#username-input').fill('Bob');
 assert(await page.locator('#prompt-rewrite-history').isHidden());
 assert.equal(await page.locator('.prompt-rewrite-exchange').count(),0);
 console.log('PASS: both models, exact history, restore preservation, stale reply, failure, pagination, mobile control layout, identity clearing.');
 await browser.close();
})().catch(error=>{console.error(error);process.exit(1)});
