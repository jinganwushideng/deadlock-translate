const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),base='http://localhost:17868',key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim(),browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage();let passed=0;const check=(v,n)=>{assert(v,n);passed++;};
 try{
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 await page.locator('[data-page="shortcuts"]').click();
 check(await page.locator('#outgoingHotkey').inputValue()==='Ctrl+Enter','saved custom binding loaded');
 await page.locator('#chordCtrl').selectOption('');await page.locator('#chordAlt').selectOption('LAlt');await page.locator('#chordKey').selectOption('Space');
 check(await page.locator('#outgoingHotkey').inputValue()==='LAlt+Space','explicit selectors retain modifier side without browser Alt delivery');
 await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="shortcuts"]').click();check(await page.locator('#outgoingHotkey').inputValue()==='LAlt+Space','recorded binding persists across reload');
 await page.locator('#chordAlt').selectOption('');await page.locator('#chordCtrl').selectOption('LCtrl');await page.locator('#chordKey').selectOption('Enter');check(await page.locator('#outgoingHotkey').inputValue()==='LCtrl+Enter','explicit Ctrl+Enter choice retains actual left-side key');
 await page.locator('#resetHotkey').click();check(await page.locator('#outgoingHotkey').inputValue()==='LAlt+Space','reset restores default');
 await page.locator('[data-page="overview"]').click();check(await page.locator('#exportDiagnostics').isEnabled(),'export button available');
 await page.locator('#exportDiagnostics').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('诊断包已保存'));check(true,'embedded page can export without Blob/download APIs');
 await page.locator('[data-page="shortcuts"]').click();await page.locator('#testHotkey').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('快捷键监听未启用'));check(true,'missing listener has actionable error');
 fs.writeFileSync(path.join(root,'artifacts/hotkey-browser-tests.json'),JSON.stringify({passed,realGameInput:false,realApi:false},null,2));console.log({passed});
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
