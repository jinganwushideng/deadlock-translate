const {chromium}=require('playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const base='http://localhost:'+(process.env.DLT_TEST_PORT||'17868'), key=fs.readFileSync(path.join(__dirname,'../artifacts/pairing.key'),'utf8').trim();
 assert.equal((await fetch(base+'/api/config')).status,401);
 assert.equal((await fetch(base+'/control/'+key+'/session',{method:'POST',headers:{Origin:'https://invalid.example'}})).status,404);
 const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();
 const browser=await chromium.launch({channel:'msedge',headless:true});const ctx=await browser.newContext({viewport:{width:1400,height:960}});const page=await ctx.newPage();
 const errors=[];page.on('pageerror',error=>errors.push(error.message));
 await page.goto(session.url);await page.waitForFunction(()=>document.querySelector('#termCount').textContent!=='0');
 assert.equal(await page.locator('#mode').inputValue(),'hybrid');
 assert.equal(await page.locator('#incomingScope').inputValue(),'all_players');
 assert.equal(await page.locator('#translateOwn').count(),0,'the self-test toggle is replaced by the scope setting');
 await page.locator('#incomingScope').selectOption('teammates');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
 await page.screenshot({path:path.join(__dirname,'../artifacts/settings-overview.png'),fullPage:true});
 await page.locator('nav button[data-page="outgoing"]').click();
 assert.equal(await page.locator('#outgoingDisplayMode').inputValue(),'translation');
 await page.locator('#outgoingDisplayMode').selectOption('bilingual');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
 await page.locator('nav button[data-page="providers"]').click();await page.locator('#model').fill('test-model');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
 await page.reload();await page.waitForFunction(()=>document.querySelector('#model').value==='test-model');
 assert.equal(await page.locator('#incomingScope').inputValue(),'teammates');
 assert.equal(await page.locator('#outgoingDisplayMode').inputValue(),'bilingual');
 await page.locator('nav button[data-page="appearance"]').click();await page.locator('#burstPreset').click();
 assert.equal(await page.locator('#fastConcurrency').inputValue(),'4');assert.equal(await page.locator('#llmConcurrency').inputValue(),'4');
 await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
 await page.reload();await page.waitForFunction(()=>document.querySelector('#fastConcurrency').value==='4');
 assert.equal(await page.locator('#outgoingConcurrency').inputValue(),'3');assert.equal(await page.locator('#outgoingQueueLimit').inputValue(),'12');
 await page.route('**/api/outgoing',route=>route.fulfill({json:{hotkeyAvailable:true,items:[{id:'failed-fixture',original:'你好',english:'Hello',sendText:'你好 | Hello',channel:'team',displayMode:'bilingual',state:'failed',error:'未能确认发送频道，未发送；原文已保留'}]}}));
 await page.locator('nav button[data-page="outgoing"]').click();
 await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('原因：未能确认发送频道'));
 assert((await page.locator('#outgoingItems').innerText()).includes('英文：Hello'));
 assert((await page.locator('#outgoingItems').innerText()).includes('保留内容（未发送）：你好 | Hello'));
 assert((await page.locator('#outgoingItems').innerText()).includes('友军'));
 await page.unroute('**/api/outgoing');
 await page.route('**/api/outgoing',route=>route.fulfill({json:{hotkeyAvailable:true,items:[{id:'party-fixture',original:'你好',english:'Hello',sendText:'你好 | Hello',channel:'party',displayMode:'bilingual',state:'submitted',error:null}]}}));
 await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('队伍（组队）'));
 assert((await page.locator('#outgoingItems').innerText()).includes('已交给游戏'));
 await page.unroute('**/api/outgoing');
 await page.locator('nav button[data-page="glossary"]').click();await page.locator('#termSearch').fill('Bebop');assert((await page.locator('#termRows tr').count())>=1);
 await page.screenshot({path:path.join(__dirname,'../artifacts/settings-terms.png'),fullPage:true});
 // A real browser exercises same-document bridge updates, JSON title return and authenticated game POST.
 const bridge=await ctx.newPage();await bridge.goto(base+'/bridge/'+key);
 const payload={session:'browser-integration',seq:1,messages:[{id:'1',text:"don't fight",sender:'fixture'}],poll:['1']};
 await bridge.goto(base+'/bridge/'+key+'#'+encodeURIComponent(JSON.stringify(payload)));
 await bridge.waitForFunction(()=>document.title.startsWith('DLT:{'));
 let response=JSON.parse((await bridge.title()).slice(4));assert.equal(response.seq,1);
 for(let i=0;i<40&&!response.results.some(r=>r.text==='别打');i++){
  payload.seq++;payload.messages=[];await bridge.goto(base+'/bridge/'+key+'#'+encodeURIComponent(JSON.stringify(payload)));await bridge.waitForFunction(seq=>{try{return JSON.parse(document.title.slice(4)).seq===seq;}catch{return false;}},payload.seq);response=JSON.parse((await bridge.title()).slice(4));
 }
 assert(response.results.some(r=>r.text==='别打'));
 // Navigation URLs are decoded once by URLSearchParams, including literal percent signs.
 await page.evaluate(async()=>{const c=await(await fetch('/api/config')).json();c.settings.enabled=false;await fetch('/api/config',{method:'PUT',headers:{'Content-Type':'application/json','X-DLT-Request':'1'},body:JSON.stringify(c.settings)});});
 let percentBody;bridge.on('request',r=>{if(r.url().includes('/game/')&&r.method()==='POST')percentBody=r.postDataJSON();});
 const percent={session:'percent-regression',seq:901,messages:[{id:'pct',text:'50% HP + %20 literal',sender:'fixture'}],poll:['pct']};
 await bridge.goto(base+'/bridge/'+key+'?d='+encodeURIComponent(JSON.stringify(percent)));await bridge.waitForFunction(()=>{try{return JSON.parse(document.title.slice(4)).seq===901;}catch{return false;}});
 assert.equal(percentBody.messages[0].text,'50% HP + %20 literal');
 await page.evaluate(async()=>{const c=await(await fetch('/api/config')).json();c.settings.enabled=true;await fetch('/api/config',{method:'PUT',headers:{'Content-Type':'application/json','X-DLT-Request':'1'},body:JSON.stringify(c.settings)});});
 // HTML-looking text must stay inert in the settings result renderer.
 await page.locator('nav button[data-page="overview"]').click();await page.locator('#testText').fill("don't fight");await page.locator('#test').click();await page.waitForFunction(()=>document.querySelector('#testResult').textContent.includes('别打'));
 const csrf=await page.evaluate(async()=>{const r=await fetch('/api/config',{method:'PUT',headers:{'Content-Type':'application/json'},body:'{}'});return r.status;});assert.equal(csrf,403);
 await page.setViewportSize({width:700,height:1000});await page.screenshot({path:path.join(__dirname,'../artifacts/settings-mobile.png'),fullPage:true});
 assert.deepEqual(errors,[]);
 // Restore test-only model setting; no live provider credential is used by this test.
 await page.evaluate(async()=>{const c=await(await fetch('/api/config')).json();c.settings.model='deepseek-chat';c.settings.translateOwn=false;c.settings.incomingScope='all_players';c.settings.outgoingDisplayMode='translation';await fetch('/api/config',{method:'PUT',headers:{'Content-Type':'application/json','X-DLT-Request':'1'},body:JSON.stringify(c.settings)});});
 const result={passed:24,browser:'Microsoft Edge headless',at:new Date().toISOString(),bridge:'fragment/query bridge, failure reasons, channel labels and concurrency preset persistence verified; not game CEF'};
 fs.writeFileSync(path.join(__dirname,'../artifacts/browser-tests.json'),JSON.stringify(result,null,2));console.log(result);await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
