// Actual product document embedded over changing native-surface stand-ins.
// This verifies Chromium canvas compositing, not Steam's HTML texture renderer.
const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const {chromium}=require('playwright'),{PNG}=require('pngjs');
const fixture=path.join(__dirname,'native-editor-ui.test.cjs'),mod=new Module(fixture,module);
mod.filename=fixture;mod.paths=Module._nodeModulePaths(__dirname);mod._compile(fs.readFileSync(fixture,'utf8').split('(async()=>{')[0]+'\nmodule.exports={server,errors};',fixture);
const w=mod.exports,checks=[],samples=[];
function check(value,name){assert(value,name);checks.push(name);console.log('PASS '+name);}
(async()=>{let browser;try{
 await new Promise(r=>w.server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+w.server.address().port;
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1440,height:900}});page.on('pageerror',e=>w.errors.push(e.message));
 await page.setContent('<html style="color-scheme:normal;background:#187547"><body style="margin:0"><iframe style="position:fixed;inset:0;width:100%;height:100%;border:0"></iframe></body></html>');
 await page.locator('iframe').evaluate((el,url)=>el.src=url,base+'/in-game/fixture?windowProtocol=2&windowLoad=1&surfaceProbe=5');
 const frame=await page.locator('iframe').elementHandle().then(el=>el.contentFrame());
 await frame.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 await frame.locator('nav [data-page="appearance"]').click();
 async function pixel(color){await page.evaluate(c=>document.documentElement.style.background=c,color);await page.waitForTimeout(60);const png=PNG.sync.read(await page.screenshot());const i=(20*png.width+20)*4;return [...png.data.subarray(i,i+4)];}
 for(const mode of ['static','translucent','blur']){
  await frame.locator('#settingsBackgroundMode').selectOption(mode);await frame.waitForFunction(m=>document.documentElement.dataset.settingsBackground===m,mode);
  const green=await pixel('#187547'),red=await pixel('#b94020'),difference=green.reduce((sum,v,i)=>sum+Math.abs(v-red[i]),0);samples.push({mode,green,red,difference});
  check(mode==='static'?difference===0:difference>40,mode==='static'?'static texture remains opaque above a changing scene':mode+' genuinely reveals the changing scene through the actual embedded product page');
  check(await frame.evaluate(()=>getComputedStyle(document.querySelector('h1')).opacity==='1'&&getComputedStyle(document.querySelector('select')).colorScheme==='dark'),'mode '+mode+' preserves sharp text and dark control appearance');
 }
 await frame.evaluate(()=>document.documentElement.style.setProperty('color-scheme','dark','important'));
 const blockedGreen=await pixel('#187547'),blockedRed=await pixel('#b94020');
 check(JSON.stringify(blockedGreen)===JSON.stringify(blockedRed),'the former dark root scheme reproduces an opaque embedded canvas despite transparent CSS');
 await frame.evaluate(()=>document.documentElement.style.removeProperty('color-scheme'));
 check(JSON.stringify(await pixel('#187547'))!==JSON.stringify(await pixel('#b94020')),'removing the root scheme regression restores compositing immediately');
 await frame.locator('#sceneSurfaceProbe').click();await frame.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:surfaceprobe:1:'));
 check(await frame.evaluate(()=>document.documentElement.hasAttribute('data-surface-probe')&&getComputedStyle(document.body,'::before').backgroundColor==='rgba(0, 0, 0, 0)'),'deliberate native alpha probe clears only background layers');
 await frame.waitForFunction(()=>!document.documentElement.hasAttribute('data-surface-probe'));
 check((await frame.title()).startsWith('DLTSETTINGS:ready:')&&(await frame.locator('#sceneSurfaceProbeStatus').textContent()).includes('检查结束'),'probe deadline restores ordinary readiness and a useful result instruction');
 const external=await browser.newPage();await external.goto(base+'/');await external.waitForSelector('#autoSaveStatus');
 check(!await external.locator('#sceneSurfaceProbe').isVisible()&&!await external.locator('#sceneSurfaceProbeStatus').isVisible(),'unsupported external browsers expose neither native probe action nor misleading instructions');
 const modern=await browser.newPage();await modern.clock.install();await modern.goto(base+'/in-game/fixture?windowProtocol=2&windowLoad=2&surfaceProbe=5');await modern.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 await modern.locator('nav [data-page="appearance"]').click();await modern.locator('#sceneSurfaceProbe').click();await modern.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:surfaceprobe:1:'));
 check((await modern.locator('#sceneSurfaceProbeStatus').textContent()).includes('约六秒后恢复'),'the current native protocol describes only the short self-restoring check');
 await modern.clock.fastForward(6200);check((await modern.title()).startsWith('DLTSETTINGS:surfaceprobe:'),'new protocol retains its deliberate probe until the final native phase finishes');
 await modern.clock.fastForward(400);await modern.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 check(!await modern.evaluate(()=>document.documentElement.hasAttribute('data-surface-probe'))&&(await modern.locator('#sceneSurfaceProbeStatus').textContent()).includes('导出日志反馈'),'probe restores and gives relevant recovery guidance instead of experimental instructions');
 const oldNative=await browser.newPage();await oldNative.goto(base+'/in-game/fixture?windowProtocol=2&windowLoad=3&surfaceProbe=4');await oldNative.waitForSelector('#autoSaveStatus');check(!await oldNative.locator('#sceneSurfaceProbe').isVisible(),'retired native probe versions cannot trigger a mismatched experiment');
 check(w.errors.length===0,'embedded alpha and probe scenarios emit no script errors: '+JSON.stringify(w.errors));
 const task=path.join(__dirname,'../artifacts/scene-composition-20261028');fs.writeFileSync(path.join(task,'surface-alpha-tests.json'),JSON.stringify({passed:checks.length,checks,samples,realGame:false,cloudCalls:0,scope:'actual product page in real Edge with changing parent pixels; Steam texture alpha and world blur require live game acceptance'},null,2));
}finally{if(browser)await browser.close();w.server.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
