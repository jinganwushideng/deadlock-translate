const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),Module=require('node:module');
const {pathToFileURL}=require('node:url');
const root=path.resolve(__dirname,'..'),task=path.join(root,'artifacts/settings-background-modes-20261027');
let passed=0;const checks=[];function check(value,name){assert(value,name);passed++;checks.push(name);}
const fixture=path.join(__dirname,'game-window.test.cjs');
const mod=new Module(fixture,module);mod.filename=fixture;mod.paths=Module._nodeModulePaths(__dirname);
mod._compile(fs.readFileSync(fixture,'utf8').split('function check(name,fn)')[0]+'\nmodule.exports=world;',fixture);
const world=mod.exports,w=world(),classes=new Set();
w.host.SetHasClass=(name,on)=>on?classes.add(name):classes.delete(name);
function title(value){w.page.title=value;w.handlers.HTMLTitle(w.page,value);w.tick();}
w.tick();title('DLTSETTINGS:ready:1:background:blur');
check(classes.has('DltBackgroundBlur')&&!classes.has('DltBackgroundTranslucent'),'native blur request uses one owned host class');
title('DLTSETTINGS:ready:2:background:translucent');
check(!classes.has('DltBackgroundBlur')&&classes.has('DltBackgroundTranslucent'),'switch away from blur immediately removes world-blur class');
title('DLTSETTINGS:ready:3:background:static');check(classes.size===0,'static leaves no scene effect class');
title('DLTSETTINGS:ready:4:background:blur');w.sandbox.DLTSettingsClose();check(classes.size===0&&!w.visible,'close clears blur before owned host deletion');
const old=world();old.tick();old.page.title='DLTSETTINGS:ready:1:background:blur';old.tick();check(old.visible&&old.status.text.includes('Esc'),'unavailable optional background API preserves usable settings');
const guarded=world();guarded.tick();guarded.page.title='DLTSETTINGS:ready:1:background:static:save:error';guarded.tick();guarded.sandbox.DLTSettingsClose('escape');check(guarded.visible&&guarded.helpText.text.includes('未保存修改'),'native Escape preserves failed save drafts with relevant recovery guidance');guarded.page.title='DLTSETTINGS:ready:2:background:static:save:clean';guarded.tick();guarded.sandbox.DLTSettingsClose('escape');check(!guarded.visible,'native Escape closes once the page reports a clean saved state');
const nativeCss=fs.readFileSync(path.join(root,'mod/panorama/styles/dlt_settings_window.css'),'utf8');
check((nativeCss.match(/world-blur: ingameHudBlur/g)||[]).length===1,'native blur has a single surface with the game native preset');
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1920,height:1200}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(pathToFileURL(path.join(root,'artifacts/ui-component-preview-20261027/index.html')).href);await page.waitForFunction(()=>window.preview?.active==='overview');
 await page.locator('nav [data-page="appearance"]').click();
 check(await page.locator('#settingsBackgroundMode option').count()===3,'three actual selectable modes appear in display settings');
 for(const mode of ['static','translucent','blur']){
  await page.locator('#settingsBackgroundMode').selectOption(mode);await page.mouse.move(1900,20);await page.waitForTimeout(100);
  const state=await page.evaluate(()=>({mode:document.documentElement.dataset.settingsBackground,selected:document.querySelectorAll('.page:not([hidden])>.is-current-group').length,textOpacity:getComputedStyle(document.querySelector('#settingsBackgroundCard h3')).opacity,filter:getComputedStyle(document.querySelector('#settingsBackgroundCard')).filter,blur:getComputedStyle(document.body,'::before').backdropFilter}));
  check(state.mode===mode&&state.selected===1&&state.textOpacity==='1'&&state.filter==='none','mode '+mode+' keeps text sharp and selection stable');
  check(mode==='blur'?state.blur.includes('8px'):state.blur==='none','only blur demonstration owns a filter: '+mode);
  await page.screenshot({path:path.join(task,'preview-'+mode+'.png')});
 }
 await page.evaluate(()=>window.DltSettingsBackground.apply('blur',{nativeAvailable:false,previewScene:false}));
 check(await page.evaluate(()=>document.documentElement.dataset.settingsBackground==='static'&&document.documentElement.dataset.settingsBackgroundRequested==='blur'),'external browser retains chosen blur and explicitly falls back to static');
 check((await page.locator('#settingsBackgroundStatus').textContent()).includes('暂用静态'),'fallback is visible, not silently reported as real blur');
 await page.evaluate(()=>window.DltSettingsBackground.apply('translucent',{nativeAvailable:true,previewScene:false}));
 check(await page.evaluate(()=>getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'&&getComputedStyle(document.body,'::before').backdropFilter==='none'),'native transparency exposes the compositor without CSS blur');
 await page.evaluate(()=>window.DltSettingsBackground.apply('blur',{nativeAvailable:true}));
 check(await page.evaluate(()=>getComputedStyle(document.body,'::before').backdropFilter==='none'),'real game mode delegates blur to Panorama, no duplicate CSS pass');
 await page.evaluate(()=>window.DltSettingsBackground.apply('untrusted-input',{nativeAvailable:true}));check(await page.evaluate(()=>document.documentElement.dataset.settingsBackground==='static'),'unknown mode returns to safe static');
 check(errors.length===0,'no browser errors');
 fs.writeFileSync(path.join(task,'mode-tests.json'),JSON.stringify({passed,checks,errors,nativeScope:'mocked Panorama lifecycle plus actual current-game CSS evidence; game rendering/FPS not verified',browserScope:'real Edge renderer; synthetic backdrop only',deployed:false},null,2));console.log(JSON.stringify({passed,errors}));
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
