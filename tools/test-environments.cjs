// Runs two real Kestrel processes with disposable data and no provider calls.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const binary = path.join(root, 'MultiImageClient/bin/Debug/net10.0/MultiImageClient.dll');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'mic-environment-browser-'));
const processes = [];
const sha = value => crypto.createHash('sha256').update(value).digest('hex');
async function freePort() {
  const server = net.createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}
async function start(id) {
  const directory = path.join(temporary, id); fs.mkdirSync(directory);
  const port = await freePort();
  const salt = crypto.randomBytes(16);
  const passwordHash = `pbkdf2-sha256$600000$${salt.toString('base64')}$${crypto.pbkdf2Sync('fixture-password', salt, 600000, 32, 'sha256').toString('base64')}`;
  const auth = {version:2, enabled:true, secret:crypto.randomBytes(32).toString('hex'), accounts:[{username:'ernieMultiZone',passwordHash}]};
  const accountId = crypto.randomUUID().replaceAll('-', ''); const secret = crypto.randomBytes(32).toString('base64url');
  const link = `https://environment.test/${id}/enter.html#${accountId}.${secret}`;
  const links = {version:1, accounts:[{id:accountId, login:'ernieMultiZone', displayName:'Ernie',tokenHash:sha(secret),revoked:false}],defaultGenerators:['gpt2']};
  fs.writeFileSync(path.join(directory,'auth.json'), JSON.stringify(auth));
  fs.writeFileSync(path.join(directory,'links.json'), JSON.stringify(links));
  const settings = { LogFilePath:path.join(directory,'log.txt'), ImageDownloadBaseFolder:path.join(directory,'saves'),
    UiAuthFilePath:path.join(directory,'auth.json'), UiLoginLinksFilePath:path.join(directory,'links.json'),
    UiEnvironmentId:id, UiEnvironmentName:'Studio '+id, UiPublicBaseUrl:`https://environment.test/${id}`,
    UiMaxConcurrentJobs:1, UiMaxConcurrentGenerators:1, UiMaxPendingJobs:2, EnableGenerationArchive:true,
    OpenAIApiKey:'sk-'+crypto.randomBytes(32).toString('hex'), EnableLocalGenerators:false, PromptFiles:[] };
  fs.writeFileSync(path.join(directory,'settings.json'),JSON.stringify(settings));
  const child=spawn('dotnet',[binary,'--ui','--ui-port',String(port),'--ui-no-open'],{
    cwd:directory,env:{...process.env,MULTIIMAGECLIENT_SETTINGS:path.join(directory,'settings.json')},windowsHide:true,stdio:'ignore'});
  processes.push(child);
  let ready=false;
  for(let i=0;i<80;i++) {
    if(child.exitCode!==null) throw new Error(`Fixture ${id} exited before startup.`);
    try { ready=(await fetch(`http://127.0.0.1:${port}/healthz`)).ok; } catch {}
    if(ready)break; await new Promise(resolve=>setTimeout(resolve,250));
  }
  assert(ready,`Fixture ${id} did not start.`);return {port,link,directory};
}
(async()=>{
  let browser;
  try {
    const one=await start('one'),two=await start('two');
    browser=await chromium.launch({headless:true,channel:process.env.MIC_BROWSER_CHANNEL||'chrome'});
    const context=await browser.newContext({viewport:{width:1150,height:900}});
    await context.route('https://environment.test/**', async route=>{
      const request=route.request(),url=new URL(request.url()),parts=url.pathname.split('/');
      const server=parts[1]==='one'?one:parts[1]==='two'?two:null;
      if(!server)return route.fulfill({contentType:'text/html',body:'<!doctype html><title>Legacy</title>'});
      const headers={...await request.allHeaders(),'x-forwarded-proto':'https'};delete headers.host;delete headers['content-length'];
      const response=await fetch(`http://127.0.0.1:${server.port}/${parts.slice(2).join('/')}${url.search}`,{
        method:request.method(),headers,body:['GET','HEAD'].includes(request.method())?undefined:request.postDataBuffer(),redirect:'manual'});
      const resultHeaders=Object.fromEntries(response.headers);delete resultHeaders['content-encoding'];delete resultHeaders['content-length'];
      await route.fulfill({status:response.status,headers:resultHeaders,body:Buffer.from(await response.arrayBuffer())});
    });
    const page=await context.newPage();
    await page.goto('https://environment.test/legacy/');
    await page.evaluate(()=>localStorage.setItem('mic_username','Legacy user'));
    await page.goto(one.link); await page.waitForURL('https://environment.test/one/');
    await page.waitForFunction(()=>document.getElementById('username-input')?.value==='Ernie');
    assert.notEqual(await page.evaluate(()=>localStorage.getItem('mic_username')),'Legacy user');
    await page.evaluate(()=>localStorage.setItem('isolation-fixture','one'));
    const second=await context.newPage();await second.goto(two.link);await second.waitForURL('https://environment.test/two/');
    await second.waitForFunction(()=>document.getElementById('username-input')?.value==='Ernie');
    assert.equal(await second.evaluate(()=>localStorage.getItem('isolation-fixture')),null);
    const cookies=await context.cookies();assert(cookies.some(c=>c.name==='mic_auth_one'&&c.path==='/one/'&&c.httpOnly&&c.secure));
    assert(cookies.some(c=>c.name==='mic_auth_two'&&c.path==='/two/'));
    await page.goto('https://environment.test/one/people.html');
    await page.waitForSelector('#management:not([hidden])');
    await page.locator('#name').fill('Alice');await page.getByRole('button',{name:'Add person',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('link').value.includes('/enter.html#'));
    const aliceLink=await page.locator('#link').inputValue();
    const alice=await context.newPage();await alice.goto(aliceLink);await alice.waitForURL('https://environment.test/one/');
    await alice.waitForFunction(()=>document.getElementById('username-input')?.value==='Alice');
    assert.equal(await alice.evaluate(()=>localStorage.getItem('isolation-fixture')),null);
    assert.equal(await alice.evaluate(async()=> (await fetch('api/people')).status),403);
    assert.equal(await alice.evaluate(async()=> (await (await fetch('api/jobs')).json()).jobs.length),0);
    assert.equal(await second.evaluate(async()=> (await fetch('api/people')).status),200);
    // An owner tab with stale identity cannot create accounts as the new session.
    assert.equal(await page.evaluate(async()=> (await fetch('api/people',{method:'POST',headers:{'X-Mic-Manage':'1'},body:new URLSearchParams({name:'Stale'})})).status),401);
    await page.goto(one.link);await page.waitForURL('https://environment.test/one/');
    await page.goto('https://environment.test/one/people.html');await page.waitForSelector('#management:not([hidden])');
    page.on('dialog',dialog=>dialog.accept());
    const row=page.locator('.person').filter({hasText:'Alice'});
    await row.getByRole('button',{name:'Replace login link',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('link').value.length>0);
    const replacement=await page.locator('#link').inputValue();assert.notEqual(replacement,aliceLink);
    await alice.goto(aliceLink);await alice.waitForFunction(()=>document.getElementById('status')?.textContent.includes('invalid or revoked'));
    await row.getByRole('button',{name:'Revoke link',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('#accounts').textContent.includes('revoked'));
    await alice.goto(replacement);await alice.waitForFunction(()=>document.getElementById('status')?.textContent.includes('invalid or revoked'));
    // Another environment's login token cannot enter this environment.
    await alice.goto(replacement.replace('/one/','/two/'));await alice.waitForFunction(()=>document.getElementById('status')?.textContent.includes('invalid or revoked'));
    await page.locator('#provider-defaults input[value="gpt2"]').uncheck();
    await page.getByRole('button',{name:'Save defaults',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('status').textContent==='Environment defaults saved.');
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(one.directory,'links.json'))).defaultGenerators,[]);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(two.directory,'links.json'))).defaultGenerators,['gpt2']);
    await page.setViewportSize({width:390,height:850});await page.screenshot({path:path.join(temporary,'people-mobile.png')});
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Mobile page overflows.');
    await page.locator('.person').filter({hasText:'Ernie'}).getByRole('button',{name:'Replace login link',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('status').textContent==='Copy your new link, then open it to continue.');
    const newOwnerLink=await page.locator('#link').inputValue();
    assert.notEqual(newOwnerLink,one.link);
    assert(await page.locator('#copy').isEnabled());
    await page.goto(newOwnerLink);await page.waitForURL('https://environment.test/one/');
    await page.waitForFunction(()=>document.getElementById('username-input')?.value==='Ernie');
    await page.goto('https://environment.test/legacy/');assert.equal(await page.evaluate(()=>localStorage.getItem('mic_username')),'Legacy user');
    console.log('PASS: two real environments, automatic login, separate cookies/configuration, owner management, revocation, defaults, legacy preservation.');
    console.log('Browser artifacts: '+temporary);
  } finally {
    if(browser)await browser.close();
    for(const child of processes) {
      if(child.exitCode!==null)continue;
      const exited=new Promise(resolve=>child.once('exit',resolve));child.kill();await exited;
    }
  }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
