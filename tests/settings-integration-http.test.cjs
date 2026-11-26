const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'src/Bridge/bin/Debug/net10.0-windows'),task=path.join(root,'artifacts/ui-integration-20261027'),state=path.join(task,'http-'+Date.now());fs.mkdirSync(state,{recursive:true});
const pair=path.join(state,'pairing.key'),key=crypto.randomBytes(24).toString('hex');fs.writeFileSync(pair,key);let child,browser,base,headers;const checks=[],sleep=ms=>new Promise(r=>setTimeout(r,ms));
const check=(v,n)=>{assert(v,n);checks.push(n);console.log('PASS '+n);};
const port=()=>new Promise(resolve=>{const server=net.createServer();server.listen(0,'127.0.0.1',()=>{const p=server.address().port;server.close(()=>resolve(p));});});
async function api(name,method='GET',value,revision){const r=await fetch(base+'/api/'+name,{method,headers:{...headers,...revision?{'If-Match':'"'+revision+'"'}:{}},body:value===undefined?undefined:JSON.stringify(value)});return {status:r.status,data:await r.json()};}
(async()=>{try{
 const p=await port(),tls=await port();base='http://localhost:'+p;child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pair,'--port',String(p),'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});const log=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(log);child.stderr.pipe(log);
 for(let i=0;i<100;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 const login=await fetch(base+'/in-game/'+key),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 browser=await chromium.launch({channel:'msedge',headless:true});const context=await browser.newContext({viewport:{width:1920,height:1200}});await context.addCookies([{name:cookie.split('=')[0],value:cookie.split('=')[1],url:base}]);
 const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto(base+'/');await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 if(process.argv.includes('--glossary')){
  await page.locator('nav [data-page="glossary"]').click();
  const aliases=['词库测试黑子','词库测试岚梦'];
  for(const alias of aliases){
   await page.locator('#addTerm').click();
   const row=page.locator('#termRows tr').first();
   await row.getByRole('textbox',{name:'匹配词',exact:true}).fill(alias);
   await row.getByRole('textbox',{name:'中文译名',exact:true}).fill('岚梦');
   await row.getByRole('textbox',{name:'标准英文名',exact:true}).fill('Haze');
  }
  const termFile=path.join(state,'terms.json');
  // A directory at the write target exercises the actual backend failure path.
  fs.mkdirSync(termFile);
  await page.locator('#saveTerms').click();
  await page.waitForFunction(()=>document.querySelector('#termSaveStatus').textContent.includes('无法写入本机词库'));
  check(await page.locator('#termRows input').first().inputValue()===aliases[1]&&!(await api('terms')).data.some(t=>aliases.includes(t.source)),'write failure retains the editable draft and active terms');
  fs.rmdirSync(termFile);
  await page.locator('#saveTerms').click();
  await page.waitForFunction(()=>document.querySelector('#termSaveStatus').textContent.includes('词库已保存'));
  const diskTerms=JSON.parse(fs.readFileSync(termFile,'utf8'));
  check(aliases.every(alias=>diskTerms.some(t=>t.source===alias&&t.english==='Haze')),'two source aliases save against the real API with the same English name');
  const nicknameTest=await api('test','POST',{direction:'outgoing',provider:'fast',text:aliases.join('、')});
  check(nicknameTest.status===200&&nicknameTest.data.translation==='Haze, Haze','outgoing translation uses both aliases immediately without reloading');
  await page.reload();
  await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
  await page.locator('nav [data-page="glossary"]').click();
  check(await page.locator('#termRows input').first().inputValue()===aliases[1],'reopening settings reads the saved aliases');
  const originalYamato=(await api('terms')).data.find(t=>t.source==='Yamato');
  await page.locator('#addTerm').click();
  const draft=page.locator('#termRows tr').first();
  await draft.getByRole('textbox',{name:'匹配词',exact:true}).fill('Yamato');
  await draft.getByRole('textbox',{name:'中文译名',exact:true}).fill(originalYamato.target);
  await draft.getByRole('textbox',{name:'标准英文名',exact:true}).fill('Yamato');
  await page.locator('#saveTerms').click();
  await page.waitForFunction(()=>document.querySelector('#termSaveStatus').textContent.includes('已存在'));
  check(await draft.getByRole('textbox',{name:'匹配词',exact:true}).inputValue()==='Yamato','an identical duplicate is still flagged in the new row');
  await draft.getByRole('textbox',{name:'中文译名',exact:true}).fill('尖尖头');
  await page.locator('#saveTerms').click();
  await page.waitForFunction(()=>document.querySelector('#termSaveStatus').textContent.includes('尖尖头 → Yamato'));
  let saved=(await api('terms')).data;
  assert.deepEqual(saved.find(t=>t.source==='Yamato'),originalYamato);
  check(saved.some(t=>t.source==='尖尖头'&&t.english==='Yamato'),'the reported three fields save as a new alias and preserve the canonical row');
  const outgoing={direction:'outgoing',provider:'quality'};
  const solo=await api('test','POST',{...outgoing,text:'尖尖头'});
  const callout=await api('terms/preview','POST',{...outgoing,text:'集火尖尖头'});
  check(solo.status===200&&solo.data.translation==='Yamato'&&callout.data.prepared==='集火Yamato','the saved nickname is used for direct sending and inside a callout');
  await page.locator('#addTerm').click();
  await draft.getByRole('textbox',{name:'匹配词',exact:true}).fill('尖头哥');
  await draft.getByRole('textbox',{name:'标准英文名',exact:true}).fill('Yamato');
  await page.locator('#saveTerms').click();
  await page.waitForFunction(()=>document.querySelector('#termSaveStatus').textContent.includes('尖头哥 → Yamato'));
  saved=(await api('terms')).data;
  check(saved.some(t=>t.source==='尖头哥'&&t.target===originalYamato.target),'a new nickname can take its Chinese translation from a known English name');
  check(errors.length===0,'glossary settings has no browser errors');
  console.log(JSON.stringify({passed:checks.length,cloudCalls:0}));return;
 }
 check(await page.locator('nav [data-page]').count()===9,'nine real navigation categories');check(await page.locator('.subnav [data-section]').count()===await page.locator('section.page>article.card').count(),'every subsection points to a real card');
 for(const id of ['overview','incoming','outgoing','shortcuts','providers','glossary','appearance','performance','diagnostics']){await page.locator('nav [data-page="'+id+'"]').click();await page.waitForFunction(p=>document.getElementById(p).hidden===false,id);check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'no horizontal root overflow: '+id);}
 await page.locator('nav [data-page="outgoing"]').click();await page.locator('#subnav-outgoing [data-section]').last().click();await page.waitForFunction(()=>!window.DltSettingsNavigation.moving);const top=await page.locator('#contentScroll').evaluate(e=>e.scrollTop);await page.locator('#subnav-outgoing [data-section]').last().click();check(await page.locator('#contentScroll').evaluate(e=>e.scrollTop)===top,'same-subsection locate does not reset to top');check(await page.locator('#outgoing>.is-current-group').count()===1,'only selected subsection has a backing');
 await page.locator('nav [data-page="providers"]').click();await page.locator('#model').fill('local-draft');await page.waitForTimeout(800);check((await api('config')).data.settings.model!=='local-draft','focused text is not saved mid-edit');await page.locator('#pageTitle').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');check((await api('config')).data.settings.model==='local-draft','text saves after editing ends');
 let delayed=false;await page.route('**/api/config',async route=>{if(route.request().method()==='PUT'&&!delayed){delayed=true;await sleep(900);}await route.continue();});
 await page.locator('nav [data-page="appearance"]').click();await page.locator('#incomingShowHeroName').uncheck();await page.waitForTimeout(650);await page.locator('#incomingShowAccountName').uncheck();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');let config=(await api('config')).data;check(!config.settings.incomingShowHeroName&&!config.settings.incomingShowAccountName,'late save response preserves and subsequently saves newer edits');await page.unroute('**/api/config');
 await page.locator('nav [data-page="providers"]').click();await page.locator('#model').fill('');await page.locator('#pageTitle').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').classList.contains('error'));await page.locator('#closeSettings').click();check(await page.locator('.shell').isVisible()&&await page.locator('#model').inputValue()==='','invalid save blocks close and keeps the draft');await page.locator('#model').fill('recovered-model');await page.locator('#pageTitle').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 let fail=true;await page.route('**/api/config',async route=>{if(fail&&route.request().method()==='PUT'){fail=false;return route.fulfill({status:503,json:{error:'fixture unavailable'}});}return route.continue();});await page.locator('#llmStreaming').uncheck();await page.waitForSelector('#retrySave:visible');check(!await page.locator('#llmStreaming').isChecked(),'network failure retains changed selection');await page.locator('#retrySave').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');check(!(await api('config')).data.settings.llmStreaming,'retry persists the retained draft once');await page.unroute('**/api/config');
 const other=await context.newPage();await other.goto(base+'/');await other.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 await page.locator('nav [data-page="appearance"]').click();await page.locator('#fontSize').selectOption('20');await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 await other.locator('nav [data-page="appearance"]').click();await other.locator('#fontSize').selectOption('22');await other.waitForSelector('#saveConflict:visible');check((await api('config')).data.settings.fontSize===20&&await other.locator('#fontSize').inputValue()==='22','two-window conflict preserves server value and local draft');check(await other.locator('[data-config-field="fontSize"]').count()===1,'conflicting field has an explicit per-field choice');await other.locator('#resolveSettingsConflict').click();await other.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');check((await api('config')).data.settings.fontSize===22,'explicit conflict merge saves the chosen local field');await other.close();
 await page.locator('#reloadSettings').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');await page.locator('nav [data-page="glossary"]').click();const term=page.locator('#termRows input').first();const original=await term.inputValue();await term.fill(original+'fixture');await page.locator('#pageTitle').click();await page.waitForTimeout(700);check((await api('terms')).data[0].source===original,'glossary remains manual save');await page.locator('#closeSettings').click();check(await page.locator('.shell').isVisible(),'unsaved glossary blocks closing');await page.locator('#discardTermsDraft').click();check(await page.locator('#termRows input').first().inputValue()===original,'explicit glossary discard restores only its draft');
 config=(await api('config')).data;await api('config','PUT',{...config.settings,llmKey:'fixture-only-key'},config.revision);const secret=fs.readFileSync(path.join(state,'secrets.dpapi')),secretTime=fs.statSync(path.join(state,'secrets.dpapi')).mtimeMs;config=(await api('config')).data;await api('config','PUT',{...config.settings,settingsBackgroundMode:'translucent'},config.revision);check(fs.readFileSync(path.join(state,'secrets.dpapi')).equals(secret)&&fs.statSync(path.join(state,'secrets.dpapi')).mtimeMs===secretTime,'unrelated settings do not rewrite DPAPI secrets');config=(await api('config')).data;const file=path.join(state,'settings.json'),time=fs.statSync(file).mtimeMs;await sleep(50);const noop=await api('config','PUT',config.settings,config.revision);check(noop.data.revision===config.revision&&fs.statSync(file).mtimeMs===time,'identical save performs no write or revision increment');
 await page.reload();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');await page.locator('nav [data-page="providers"]').click();await page.screenshot({path:path.join(task,'providers-integrated.png')});await page.locator('nav [data-page="overview"]').click();await page.screenshot({path:path.join(task,'overview-integrated.png')});await page.setViewportSize({width:1440,height:900});await page.locator('nav [data-page="glossary"]').click();check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'glossary uses internal scrolling at 1440');await page.screenshot({path:path.join(task,'glossary-integrated.png')});
 check(errors.length===0,'integrated product page has no browser errors');fs.writeFileSync(path.join(task,'http-tests.json'),JSON.stringify({passed:checks.length,checks,errors,state,cloudCalls:0,realGame:false},null,2));console.log(JSON.stringify({passed:checks.length,cloudCalls:0}));
}finally{if(browser)await browser.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(400);if(child.exitCode===null)child.kill();}}})().catch(e=>{console.error(e);process.exitCode=1;});
