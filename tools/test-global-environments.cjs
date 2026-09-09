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
  if (!fs.existsSync(path.join(temporary,'auth.json'))) fs.writeFileSync(path.join(temporary,'auth.json'), JSON.stringify(auth));
  if (!fs.existsSync(path.join(temporary,'links.json'))) fs.writeFileSync(path.join(temporary,'links.json'), JSON.stringify({version:1,accounts:[]}));
  const settings = { LogFilePath:path.join(directory,'log.txt'), ImageDownloadBaseFolder:path.join(directory,'saves'),
    UiAuthFilePath:path.join(temporary,'auth.json'), UiLoginLinksFilePath:path.join(temporary,'links.json'),
    UiEnvironmentRegistryPath:path.join(temporary,'registry.json'),UiEnvironmentController:id==='one',
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
  fs.writeFileSync(path.join(temporary,'registry.json'),JSON.stringify({version:1,environments:[
   {id:'one',slug:'one',name:'Original',original:true,members:[],goalLoops:true,video:true,promptRewrite:true},
   {id:'two',slug:'two',name:'Vibecoders AI Generation',original:false,members:[],goalLoops:true,video:true,promptRewrite:true}]}));
  const one=await start('one'),two=await start('two');
  browser=await chromium.launch({headless:true,channel:'chrome'});
  async function context() {
   const context=await browser.newContext();
   await context.route('https://environment.test/**',async route=>{
    const request=route.request(),url=new URL(request.url()),parts=url.pathname.split('/');
    const server=parts[1]==='one'?one:parts[1]==='two'?two:null;
    if(!server)return route.fulfill({status:404,body:''});
    const headers={...await request.allHeaders(),'x-forwarded-proto':'https'};delete headers.host;delete headers['content-length'];
    const response=await fetch(`http://127.0.0.1:${server.port}/${parts.slice(2).join('/')}${url.search}`,{
     method:request.method(),headers,body:['GET','HEAD'].includes(request.method())?undefined:request.postDataBuffer(),redirect:'manual'});
    const resultHeaders=Object.fromEntries(response.headers);delete resultHeaders['content-encoding'];delete resultHeaders['content-length'];
    await route.fulfill({status:response.status,headers:resultHeaders,body:Buffer.from(await response.arrayBuffer())});
   });return context;
  }
  const owner=await context();const page=await owner.newPage();await page.goto('https://environment.test/one/');
  assert.equal(await page.evaluate(async()=> (await fetch('api/auth/login',{method:'POST',body:new URLSearchParams({username:'ernieMultiZone',password:'fixture-password'})})).status),200);
  await page.goto('https://environment.test/two/');await page.waitForFunction(()=>document.title==='Vibecoders AI Generation');
  assert.equal(await page.locator('#environment-switcher option').count(),2);
  await page.locator('#environment-switcher').selectOption('one');await page.waitForURL('https://environment.test/one/');
  await page.locator('#environment-switcher').selectOption('two');await page.waitForURL('https://environment.test/two/');
  assert.equal(await page.evaluate(async()=> (await fetch('api/admin/summary')).status),200);
  const cookies=await owner.cookies();assert(cookies.some(c=>c.name==='mic_auth'&&c.path==='/'));
  await page.goto('https://environment.test/one/admin.html');await page.waitForSelector('#environments section');
  const created=await page.evaluate(async()=> (await fetch('api/control/accounts',{method:'POST',headers:{'X-Mic-Manage':'1'},body:new URLSearchParams({name:'Alice',environment:'two'})})).json());
  assert(created.password&&created.url&&created.username);
  const member=await context();const alice=await member.newPage();await alice.goto(created.url);await alice.waitForURL('https://environment.test/two/');
  await alice.waitForFunction(()=>document.getElementById('username-input')?.value==='Alice');
  assert.deepEqual(await alice.evaluate(()=>window.MicEnvironment.environments.map(e=>e.id)),['two']);
  assert.equal(await alice.evaluate(()=>window.MicEnvironment.adminUrl),null);
  assert.equal(await alice.locator('#environment-switcher').count(),0);
  for(const path of ['api/control/state','api/admin/summary','api/logs/poll','api/people'])
   assert.equal(await alice.evaluate(async path=>(await fetch(path)).status,path),403,path);
  assert.equal(await alice.evaluate(async()=> (await fetch('/one/api/jobs')).status),403);
  assert.equal(await alice.evaluate(async()=> (await fetch('api/activity/poll')).status),200);
  assert.equal(await alice.locator('#logs-toggle').isVisible(),false);
  assert.equal(await alice.locator('#ram-status').isVisible(),false);
  assert.equal(await alice.locator('#build-info').isVisible(),false);
  assert.equal(await alice.locator('#logout').count(),0);
  assert.equal(await alice.locator('#favorites-hint').count(),0);
  assert.equal(await alice.locator('header h1').innerText(),'Vibecoders AI Generation');
  assert.equal(await alice.locator('#getting-started').innerText(),'enter prompt, choose image generators, and click generate');
  await alice.locator('#settings-toggle').click();
  assert.equal(await alice.locator('#settings-panel #username-input').isVisible(),true);
  await alice.locator('#settings-close').click();
  await alice.screenshot({path:path.join(temporary,'member.png'),fullPage:false});
  const originalPolicy=JSON.parse(fs.readFileSync(path.join(temporary,'registry.json'))).environments.find(e=>e.id==='one');
  originalPolicy.members.push(created.username);
  assert.equal(await page.evaluate(async policy=>(await fetch('api/control/environment',{method:'POST',headers:{'X-Mic-Manage':'1','Content-Type':'application/json'},body:JSON.stringify(policy)})).status,originalPolicy),200);
  await alice.reload();await alice.waitForSelector('#environment-switcher');
  await alice.locator('#environment-switcher').selectOption('one');await alice.waitForURL('https://environment.test/one/');
  assert.equal(await alice.evaluate(async()=> (await fetch('api/activity/poll')).status),200);
  await alice.locator('#environment-switcher').selectOption('two');await alice.waitForURL('https://environment.test/two/');
  assert.equal(await alice.evaluate(async()=> (await fetch('api/discord/vibecoders')).status),403);
  const policy=JSON.parse(fs.readFileSync(path.join(temporary,'registry.json'))).environments.find(e=>e.id==='two');
  policy.nightFilter=false;
  policy.name='Renamed Studio';policy.goalLoops=false;policy.video=false;policy.promptRewrite=false;
  assert.equal(await page.evaluate(async policy=>(await fetch('api/control/environment',{method:'POST',headers:{'X-Mic-Manage':'1','Content-Type':'application/json'},body:JSON.stringify(policy)})).status,policy),200);
  await alice.reload();await alice.waitForFunction(()=>document.title==='Renamed Studio');
  assert.equal(await alice.locator('#goal-loops-link').isVisible(),false);
  assert.equal(await alice.locator('#night-toggle').isVisible(),false);
  assert.equal(await alice.evaluate(()=>{uiSettings.nightHideEnabled=true;uiSettings.nightWords='night';nightMatchers=null;return promptIsNightHidden('night');}),false);
  for(const path of ['api/goal-loops','goal.html','recap.html','api/prompt/advice/history'])
   assert.equal(await alice.evaluate(async path=>(await fetch(path)).status,path),403,path);
  const summary=await page.evaluate(async()=> (await (await fetch('/two/api/admin/summary')).json()));
  assert(summary.accounts[created.username].firstLogin);
  await alice.evaluate(async()=>fetch('api/auth/logout',{method:'POST'}));
  assert.equal(await alice.evaluate(async c=>(await fetch('api/auth/login',{method:'POST',body:new URLSearchParams({username:c.username,password:c.password})})).status,created),200);
  policy.members=[];
  assert.equal(await page.evaluate(async policy=>(await fetch('api/control/environment',{method:'POST',headers:{'X-Mic-Manage':'1','Content-Type':'application/json'},body:JSON.stringify(policy)})).status,policy),200);
  assert.equal(await alice.evaluate(async()=> (await fetch('api/jobs')).status),403);
  await page.reload();await page.waitForSelector('#environments section');await page.setViewportSize({width:1100,height:950});
  await page.screenshot({path:path.join(temporary,'admin.png'),fullPage:true});
  console.log('PASS: one owner login across environments; normal membership; private administration/logs; shared activity; editable titles; disabled APIs; persistent login records; password and link identity.');
  console.log('Artifacts: '+temporary);
 } finally {
  if(browser)await browser.close();for(const child of processes){if(child.exitCode!==null)continue;const exited=new Promise(resolve=>child.once('exit',resolve));child.kill();await exited;}
 }
})().catch(error=>{console.error(error.stack);process.exitCode=1;});
