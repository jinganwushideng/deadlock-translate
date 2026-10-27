const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),base='http://localhost:17868',key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim();
 const browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1000,height:800}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let testedDirection='';
 try {
  await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  assert.equal(await page.locator('#incomingTargetLanguage').inputValue(),'zh-Hans');
  await page.locator('[data-page="outgoing"]').click();assert.equal(await page.locator('#outgoingTargetLanguage').inputValue(),'en');
  const presets=await page.evaluate(async()=>await(await fetch('/api/languages')).json());assert.equal(presets.length,18);
  const options=await page.locator('#outgoingTargetLanguage option').evaluateAll(xs=>xs.map(x=>x.value));for(const p of presets)assert(options.includes(p.id));
  await page.locator('#outgoingTargetLanguage').selectOption('ru');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingTargetLanguage').selectOption('ja');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));assert.equal(await page.locator('#incomingTargetLanguage').inputValue(),'ja');
  await page.locator('[data-page="outgoing"]').click();assert.equal(await page.locator('#outgoingTargetLanguage').inputValue(),'ru');
  await page.locator('#outgoingTargetLanguage').selectOption('custom');assert(await page.locator('#outgoingCustomLanguageFields').isVisible());
  await page.locator('#outgoingCustomLanguage').fill('荷兰语');await page.locator('#outgoingCustomLanguageCode').fill('nl');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingTargetLanguage').selectOption('custom');await page.locator('#incomingCustomLanguage').fill('希腊语');await page.locator('#incomingCustomLanguageCode').fill('');await page.locator('#mode').selectOption('quality');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));assert.equal(await page.locator('#incomingCustomLanguage').inputValue(),'希腊语');await page.locator('[data-page="incoming"]').click();assert(await page.locator('#incomingCustomLanguageFields').isVisible());
  await page.locator('[data-page="outgoing"]').click();assert.equal(await page.locator('#outgoingCustomLanguage').inputValue(),'荷兰语');assert.equal(await page.locator('#outgoingCustomLanguageCode').inputValue(),'nl');
  await page.route('**/api/outgoing',route=>route.fulfill({json:{hotkeyAvailable:true,items:[{id:'fixture',state:'ready',original:'原文',translation:'Привет',english:'legacy text',channel:'team',displayMode:'bilingual',sendText:'原文 | Привет',targetLanguage:'ru',targetLanguageName:'俄语'}]}}));
  await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('译文（俄语）：Привет'));assert(!(await page.locator('#outgoingItems').innerText()).includes('legacy text'));
  await page.locator('[data-page="providers"]').click();await page.route('**/api/test',route=>{testedDirection=route.request().postDataJSON().direction;return route.fulfill({json:{translation:'模拟发送译文',elapsedMs:1}});});
  await page.locator('#testDirection').selectOption('outgoing');await page.locator('#test').click();await page.waitForFunction(()=>document.querySelector('#testResult').textContent.includes('模拟发送译文'));assert.equal(testedDirection,'outgoing');
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingTargetLanguage').selectOption('zh-Hans');assert(!(await page.locator('#incomingCustomLanguageFields').isVisible()));await page.locator('#mode').selectOption('hybrid');
  await page.locator('[data-page="outgoing"]').click();await page.locator('#outgoingTargetLanguage').selectOption('en');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  const config=await page.evaluate(async()=>await(await fetch('/api/config')).json());assert.equal(config.settings.incomingTargetLanguage,'zh-Hans');assert.equal(config.settings.outgoingTargetLanguage,'en');
  await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(root,'artifacts/multilanguage-060/settings-languages-mobile.png'),fullPage:true});
  assert.deepEqual(errors,[]);const report={passed:14,presets:18,scope:'real isolated backend and Edge; preset/custom save/reload, independent languages, generic send labels and test direction; no real API or game chat'};fs.writeFileSync(path.join(root,'artifacts/language-browser-tests.json'),JSON.stringify(report,null,2));console.log(report);
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
