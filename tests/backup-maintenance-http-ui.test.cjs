// Isolated local backend, disposable backup files, no game or translation API calls.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'artifacts/backup-maintenance-build'),state=path.join(root,'artifacts','backup-maintenance-http-'+Date.now());fs.mkdirSync(state,{recursive:true});
const key=crypto.randomBytes(24).toString('hex'),pairing=path.join(state,'pairing.key');fs.writeFileSync(pairing,key);
fs.writeFileSync(path.join(state,'settings.json'),'{}');
let passed=0,child,browser,base,headers;const sleep=ms=>new Promise(r=>setTimeout(r,ms));const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
const port=()=>new Promise(resolve=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>resolve(p));});});
const sha=data=>crypto.createHash('sha256').update(data).digest('hex');
function backup(n,legacy=false){
 const folder=path.join(state,'backups',`update-20261021-${String(n).padStart(9,'0')}-${n.toString(16).padStart(32,'0')}`),vpk=Buffer.from('old fixture vpk '+n);
 const files={'DeadlockTranslate.exe':Buffer.from('old fixture exe '+n),'dlt_translate_dir.vpk':vpk,'pairing.key':Buffer.from('1'.repeat(48)),'data/terms.json':Buffer.from('[]'),'wwwroot/index.html':Buffer.from('page'),'wwwroot/app.js':Buffer.from('script'),'wwwroot/style.css':Buffer.from('style')};
 for(const [relative,data] of Object.entries(files)){const file=path.join(folder,'runtime',relative);fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,data);}
 fs.writeFileSync(path.join(folder,'translation-before.vpk'),vpk);fs.writeFileSync(path.join(folder,'file-plan.json'),JSON.stringify(Object.entries(files).map(([relative,data])=>({relative,beforeHash:sha(data)}))));
 fs.writeFileSync(path.join(folder,'install-manifest-before.json'),JSON.stringify({vpkHash:sha(vpk)}));if(!legacy)fs.writeFileSync(path.join(folder,'backup-state.json'),JSON.stringify({schema:1,kind:'update',status:'succeeded'}));return folder;
}
async function api(name,method='GET',data){const r=await fetch(base+'/api/'+name,{method,headers,body:data===undefined?undefined:JSON.stringify(data)});return {status:r.status,data:await r.json()};}
(async()=>{try{
 const folders=[1,2,3,4].map(n=>backup(n)),legacy=backup(5,true);
 const other=path.join(state,'backups','steam-launch');fs.mkdirSync(other);fs.writeFileSync(path.join(other,'config.vdf'),'unrelated fixture');
 const p=await port(),tls=await port();base='http://localhost:'+p;
 child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pairing,'--port',String(p),'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});const output=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(output);child.stderr.pipe(output);
 for(let i=0;i<150;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 const login=await fetch(base+'/in-game/'+key),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 check((await fetch(base+'/api/backups')).status===401,'inventory requires authenticated settings session');
 check((await fetch(base+'/api/backups/cleanup',{method:'POST',headers:{...headers,Origin:'https://untrusted.example'},body:'{}'})).status===403,'cleanup rejects foreign origin');
 check((await api('backups/cleanup','POST',{planToken:'invalid',includeLegacy:false})).status===400,'cleanup rejects absent or invalid preview token');
 const results=await Promise.all([api('backups'),api('backups')]);check(results.some(r=>r.status===409)&&results.some(r=>r.status===200),'concurrent expensive inventories are bounded');
 const before=(await api('backups')).data;check(before.cleanupCount===2&&before.protectedCount===2,'API previews keep-two policy with historical and other backups protected');
 browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1300,height:900}});const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));let reads=0;page.on('request',r=>{if(r.url().includes('/api/backups'))reads++;});
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="diagnostics"]').click();await page.locator('#backupMaintenanceCard').scrollIntoViewIfNeeded();await sleep(600);
 check(reads===0,'opening settings does not scan backup folders in the background');check(await page.locator('#cleanBackups').isDisabled(),'cleanup disabled before preview');
 await page.locator('#inspectBackups').click();await page.waitForFunction(()=>!document.querySelector('#cleanBackups').disabled);
 check((await page.locator('#backupSummary').textContent()).includes('可清理 2 份'),'settings displays cleanup count and estimated size');
 await page.locator('#backupIncludeLegacy').check();check(await page.locator('#cleanBackups').isDisabled(),'changing historical scope invalidates old preview');
 await page.locator('#inspectBackups').click();await page.waitForFunction(()=>!document.querySelector('#cleanBackups').disabled);
 check((await page.locator('#backupSummary').textContent()).includes('可清理 3 份'),'historical files enter cleanup only after explicit opt-in');
 await page.screenshot({path:path.join(state,'backup-maintenance-before.png'),fullPage:true});
 await page.locator('#backupMaintenanceCard').screenshot({path:path.join(state,'backup-maintenance-card.png')});
 const configRaw=fs.readFileSync(path.join(state,'settings.json'));
 await page.locator('#cleanBackups').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('已清理 3 份'),{timeout:30000});
 check(!fs.existsSync(folders[0])&&!fs.existsSync(folders[1])&&!fs.existsSync(folders[2])&&fs.existsSync(folders[3])&&fs.existsSync(legacy),'UI cleanup retains two newest recovery points');
 check(fs.readFileSync(path.join(other,'config.vdf'),'utf8')==='unrelated fixture'&&fs.readFileSync(path.join(state,'settings.json')).equals(configRaw),'cleanup preserves Steam backup and live settings');
 check(await page.locator('#cleanBackups').isDisabled(),'cleanup cannot repeat an obsolete plan');
 const cleanPlan=(await api('backups')).data;backup(6);check((await api('backups/cleanup','POST',{planToken:cleanPlan.planToken})).status===409,'a changed inventory invalidates the API cleanup plan');
 fs.appendFileSync(path.join(state,'backup-maintenance.jsonl'),JSON.stringify({utc:new Date().toISOString(),action:'manual-clean',removedCount:0,removedBytes:0,failures:0,apiKey:'FORBIDDEN_SECRET',chat:'FORBIDDEN_CHAT'})+'\n');
 const exported=await api('diagnostics/export','POST',{});check(exported.status===200&&fs.existsSync(exported.data.file),'existing one-click diagnostics includes maintenance audit');
 fs.writeFileSync(path.join(state,'export-path.txt'),exported.data.file);
 await page.setViewportSize({width:640,height:760});await page.locator('#backupMaintenanceCard').scrollIntoViewIfNeeded();check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'maintenance controls fit narrow settings window');
 await page.screenshot({path:path.join(state,'backup-maintenance-narrow.png'),fullPage:true});check(errors.length===0,'settings have no browser runtime errors');
 fs.writeFileSync(path.join(state,'result.json'),JSON.stringify({passed,gameTested:false,realBackupsTouched:false},null,2));console.log(JSON.stringify({passed,artifacts:state}));
 }finally{if(browser)await browser.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(500);if(child.exitCode===null)child.kill();}}
})().catch(e=>{console.error(e);process.exitCode=1;});
