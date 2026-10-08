const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),settings={enabled:true,incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',azureRegion:'',llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',fontSize:17,bilingualNames:true,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,fastTimeoutSeconds:5,llmTimeoutSeconds:30,transport:'fragment',outgoingEnabled:true,outgoingMode:'auto',outgoingDisplayMode:'bilingual',outgoingTimeoutSeconds:30,outgoingFallbackFast:false};
 let items=[{id:'one',state:'ready',channel:'team',displayMode:'bilingual',original:'原文',english:'English',error:'',sendText:'原文 | English'}],phase='connected',active=0,maxActive=0,slow=false;
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://dlt.test/**',async route=>{
   const p=new URL(route.request().url()).pathname;
   if(p.startsWith('/api/')){
    const diagnostics={version:'0.5.0',clientVersion:'0.5.0',phase,certificate:{message:phase==='connected'?'证书正常':'请信任证书并重启 Steam',daysRemaining:20}};
    if(p==='/api/config')return route.fulfill({json:{settings,hasLlmKey:false,hasAzureKey:false}});
    if(p==='/api/terms')return route.fulfill({json:[]});
    if(p==='/api/diagnostics')return route.fulfill({json:diagnostics});
    if(p==='/api/status')return route.fulfill({json:{engine:{messages:1,pending:0,connected:true},terms:1,diagnostics}});
    if(p==='/api/outgoing'){active++;maxActive=Math.max(maxActive,active);try{if(slow)await new Promise(r=>setTimeout(r,2200));return await route.fulfill({json:{hotkeyAvailable:true,items}});}finally{active--;}}
    return route.fulfill({json:{}});
   }
   const file=p==='/app.js'?'app.js':p==='/style.css'?'style.css':'index.html';
   return route.fulfill({body:fs.readFileSync(path.join(root,'src/Bridge/wwwroot',file)),contentType:file==='app.js'?'text/javascript':file==='style.css'?'text/css':'text/html'});
  });
  await page.goto('http://dlt.test/');await page.waitForFunction(()=>document.querySelector('#diagnosticsSummary').textContent.includes('通信正常'));
  await page.locator('[data-page="outgoing"]').click();await page.locator('[data-task-id="one"]').waitFor();
  await page.evaluate(()=>{window.savedRow=document.querySelector('[data-task-id="one"]');window.savedCopy=window.savedRow.querySelector('button');window.savedCopy.focus();});
  await page.waitForTimeout(1400);
  assert(await page.evaluate(()=>window.savedRow===document.querySelector('[data-task-id="one"]')&&document.activeElement===window.savedCopy));
  items=[{...items[0],english:'Updated',sendText:'原文 | Updated'}];
  await page.waitForFunction(()=>document.querySelector('[data-task-id="one"]').textContent.includes('Updated'));
  assert(await page.evaluate(()=>window.savedRow===document.querySelector('[data-task-id="one"]')&&window.savedCopy===window.savedRow.querySelector('button')));
  items=[...items,{...items[0],id:'two'}];await page.locator('[data-task-id="two"]').waitFor();
  assert.equal(await page.locator('.outgoing-task').first().getAttribute('data-task-id'),'two');
  items=items.filter(i=>i.id!=='two');await page.waitForFunction(()=>!document.querySelector('[data-task-id="two"]'));assert(await page.evaluate(()=>window.savedRow===document.querySelector('[data-task-id="one"]')));
  slow=true;await page.waitForTimeout(3800);assert.equal(maxActive,1,'slow polling must not overlap');slow=false;await page.waitForTimeout(2300);
  await page.locator('[data-page="overview"]').click();phase='certificate-untrusted';await page.locator('#checkConnection').click();assert((await page.locator('#diagnosticsSummary').textContent()).includes('信任本机证书'));
  assert.deepEqual(errors,[]);
  fs.writeFileSync(path.join(root,'artifacts/optimization-ui-tests.json'),JSON.stringify({passed:7,scope:'mock HTTP + real Edge DOM; stable row/button identity, focus preservation, keyed updates/removal, non-overlapping polling and diagnostics'},null,2));console.log('PASS 7 incremental settings UI checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
