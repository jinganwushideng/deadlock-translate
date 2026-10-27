// Actual product page with an isolated fake backend. The native SetURL handoff is intentionally absent.
const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const {chromium}=require('playwright');const fixture=path.join(__dirname,'native-editor-ui.test.cjs'),mod=new Module(fixture,module);
mod.filename=fixture;mod.paths=Module._nodeModulePaths(__dirname);mod._compile(fs.readFileSync(fixture,'utf8').split('(async()=>{')[0]+'\nmodule.exports={server,jobs,requests,errors,get saves(){return saves;},get settings(){return settings;}};',fixture);
const w=mod.exports,checks=[];function check(value,name){assert(value,name);checks.push(name);console.log('PASS '+name);}
(async()=>{let browser;try{
 await new Promise(r=>w.server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+w.server.address().port;
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1440,height:900}});
 await page.goto(base+'/in-game/fixture?windowProtocol=2&windowLoad=1');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:')&&document.title.endsWith(':load:1'));
 // This older fixture supplies a partial config, while the real backend returns
 // the complete Settings record. Normalize once before asserting no-op writes.
 Object.assign(w.settings,await page.evaluate(()=>readSettingsDraft()));await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:')&&document.title.endsWith(':load:1'));
 check(true,'the actual page echoes its native navigation generation');
 const saves=w.saves;await page.locator('#reloadSettings').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:reload:'));
 const first=await page.title();check(first.endsWith(':background:static:save:clean:load:1'),'reload carries clean saved state and document generation');
 check(await page.locator('#reloadSettings').isDisabled(),'reload button stays disabled while the old document awaits replacement');
 await page.evaluate(()=>{for(let i=0;i<80;i++)document.getElementById('reloadSettings').dispatchEvent(new MouseEvent('click',{bubbles:true}));});await page.waitForTimeout(1300);
 check(await page.title()===first,'rapid activation and normal status polls cannot replace or duplicate the reload command');
 check(w.saves===saves,'reloading unchanged settings does not add configuration writes');
 await page.goto(base+'/in-game/fixture?windowProtocol=2&windowLoad=2');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:')&&document.title.endsWith(':load:2'));
 check(await page.locator('#reloadSettings').isEnabled(),'a completed new document permits the next deliberate reload');
 await page.locator('nav [data-page="glossary"]').click();const term=page.locator('#termRows input').first(),original=await term.inputValue();
 // Exercise unsaved glossary protection without invoking a game-native text editor.
 await term.evaluate(el=>{el.value+='-retained';el.dispatchEvent(new Event('input',{bubbles:true}));});await page.locator('#reloadSettings').click();
 check(!(await page.title()).startsWith('DLTSETTINGS:reload:')&&await term.inputValue()===original+'-retained','unsaved glossary blocks reload and retains the current edit');
 check(await page.locator('#reloadSettings').isEnabled(),'a refused reload leaves the button available after fixing the draft');await page.locator('#discardTermsDraft').click();
 await page.locator('nav [data-page="providers"]').click();await page.locator('#model').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
 const job=[...w.jobs.values()].at(-1),nativeTitle=await page.title();check(nativeTitle.endsWith(':load:2'),'native editing geometry carries the same current document generation');
 const before=w.saves;await page.locator('#reloadSettings').click();await page.waitForFunction(()=>/^DLTSETTINGS:edit:[a-f0-9]{48}:\d+:1:/.test(document.title));
 check(w.saves===before&&!(await page.title()).startsWith('DLTSETTINGS:reload:'),'reload waits for final native IME text rather than discarding the field');
 job.value='final-model-before-reload';job.state='applied';await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:reload:'));
 check(w.settings.model==='final-model-before-reload'&&w.saves===before+1,'final native field is persisted once before reload handoff');
 const second=await page.title();await page.waitForTimeout(1300);check(await page.title()===second,'save completion and heartbeat cannot overwrite the one-shot reload title');
 const fallback=await browser.newPage();await fallback.goto(base+'/');await fallback.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 let navigations=0;fallback.on('framenavigated',frame=>{if(frame===fallback.mainFrame())navigations++;});await fallback.locator('#reloadSettings').click();await fallback.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 check(navigations===1,'ordinary external browser reloads its document once');
 check(w.errors.length===0,'reload and native-return scenarios emit no browser script errors');
 fs.writeFileSync(path.join(__dirname,'../artifacts/reload-frame-disconnect-20261027/reload-ui-tests.json'),JSON.stringify({passed:checks.length,checks,realGame:false,cloudCalls:0,nativeRendererCrashTested:false},null,2));console.log(JSON.stringify({passed:checks.length}));
}finally{if(browser)await browser.close();w.server.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
