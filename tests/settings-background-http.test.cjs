// Isolated local backend. Synthetic game packets validate negotiation, never game pixels or FPS.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'dist/DeadlockTranslate-0.6.25'),state=path.join(root,'artifacts/settings-background-modes-20261027/http-'+Date.now());fs.mkdirSync(state,{recursive:true});
const pairing=path.join(state,'pairing.key'),key=crypto.randomBytes(24).toString('hex');fs.writeFileSync(pairing,key);
const sleep=ms=>new Promise(r=>setTimeout(r,ms)),checks=[];let child,browser,base,headers;
function check(ok,name){assert(ok,name);checks.push(name);}
const port=()=>new Promise(resolve=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>resolve(p));});});
async function api(name,method='GET',value){const res=await fetch(base+'/api/'+name,{method,headers,body:value===undefined?undefined:JSON.stringify(value)});return {status:res.status,data:await res.json()};}
(async()=>{try{
 const p=await port(),tls=await port();base='http://localhost:'+p;
 child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pairing,'--port',String(p),'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});const log=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(log);child.stderr.pipe(log);
 for(let i=0;i<100;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 const health=await (await fetch(base+'/health')).json();check(health.features.includes('settings-background-modes'),'published backend advertises modes');
 for(const file of ['settings-background.js','settings-background.css','settings-background-v3-flat.png']){const r=await fetch(base+'/'+file);const data=Buffer.from(await r.arrayBuffer());check(r.status===200&&data.equals(fs.readFileSync(path.join(runtime,'wwwroot',file))),'published asset has exact bytes and route: '+file);}
 const login=await fetch(base+'/in-game/'+key),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 check((await api('config')).data.settings.settingsBackgroundMode==='static','fresh backend defaults to static');
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="appearance"]').click();
 const before=(await api('config')).data.settings;
 for(const mode of ['translucent','blur','static']){
  await page.locator('#settingsBackgroundMode').selectOption(mode);await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
  const saved=(await api('config')).data.settings;check(saved.settingsBackgroundMode===mode,'real settings save persists '+mode);
  for(const prop of ['model','mode','outgoingHotkey','incomingDisplayMode','outgoingDisplayMode'])check(saved[prop]===before[prop],'background save preserves '+prop+' in '+mode);
 }
 await api('config','PUT',{...before,settingsBackgroundMode:'blur'});await page.reload();await page.waitForFunction(()=>document.querySelector('#settingsBackgroundMode').value==='blur');
 check(await page.evaluate(()=>document.documentElement.dataset.settingsBackground==='static'),'no game capability uses explicit static fallback while retaining choice');
 const game={session:'background-fixture-session',seq:1,messages:[],poll:[],settingsOpen:true,clientBuildId:health.buildId,settingsBackgroundSupported:true};
 const post=await fetch(base+'/game/'+key,{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify(game)});check(post.status===200,'matching synthetic client negotiates native background');
 await page.waitForFunction(()=>document.documentElement.dataset.settingsBackground==='blur'&&/:background:blur(?::save:clean)?$/.test(document.title));
 check(await page.evaluate(()=>getComputedStyle(document.body,'::before').backdropFilter==='none'),'game page requests native blur without a second HTML blur');
 await page.goto(base+'/');await page.waitForFunction(()=>document.querySelector('#settingsBackgroundMode').value==='blur');check(await page.evaluate(()=>document.documentElement.dataset.settingsBackground==='static'),'normal external browser never claims a game backdrop');
 check((await api('config','PUT',{...before,settingsBackgroundMode:'invalid'})).status===400,'invalid persisted mode is rejected');
 const final=JSON.parse(fs.readFileSync(path.join(state,'settings.json'),'utf8'));check(final.settingsBackgroundMode==='blur','rejected update leaves persisted mode intact');check(errors.length===0,'actual published page has no browser script errors');
 fs.writeFileSync(path.join(root,'artifacts/settings-background-modes-20261027/http-tests.json'),JSON.stringify({passed:checks.length,checks,errors,buildId:health.buildId,state,cloudCalls:0,realGame:false,gamePackets:'synthetic isolated client only',deployed:false},null,2));console.log(JSON.stringify({passed:checks.length,buildId:health.buildId,cloudCalls:0}));
}finally{if(browser)await browser.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(300);if(child.exitCode===null)child.kill();}}})().catch(e=>{console.error(e);process.exitCode=1;});
