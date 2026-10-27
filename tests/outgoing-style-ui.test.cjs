const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');let passed=0,saved=null;
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'quality',fastProvider:'bing',outgoingTargetLanguage:'en',outgoingDisplayMode:'translation',outgoingMode:'auto',fontSize:17,scanIntervalMs:200,fastConcurrency:4,llmConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'',transport:'fragment'};
const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
const server=http.createServer(async(req,res)=>{res.setHeader('Content-Type','application/json');const url=req.url.split('?')[0];
 if(url==='/api/config'&&req.method==='PUT'){let body='';for await(const c of req)body+=c;saved=JSON.parse(body);Object.assign(settings,saved);res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));}
 else if(url==='/api/config')res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));
 else if(url==='/api/terms')res.end('[]');
 else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:1312,diagnostics:{phase:'connected',certificate:{state:'ready'},sending:{state:'idle'},gamePrivileges:{detected:false}}}));
 else if(url.startsWith('/api/hotkey/'))res.end('{}');
 else if(url==='/app.js'||url==='/style.css'){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1120,height:900}});const errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 const owner=async id=>page.locator('#'+id).evaluate(el=>el.closest('section.page').id);
 check(await owner('adminRestart')==='diagnostics'&&await owner('stop')==='diagnostics'&&await owner('copyAdminLaunch')==='diagnostics','runtime permissions launch and stop grouped in diagnostics');
 check(await owner('llmConcurrency')==='performance'&&await owner('reserveSendSlot')==='performance'&&await owner('fastTimeoutSeconds')==='performance','shared service limits stay in performance and queue page');
 check(await owner('outgoingUseContext')==='outgoing'&&await owner('outgoingTone')==='outgoing'&&await owner('outgoingEnglishCase')==='outgoing','send context tone and casing belong to outgoing');
 check(await owner('scanIntervalMs')==='performance'&&await owner('transport')==='diagnostics','scan budget and bridge troubleshooting have separate locations');
 await page.locator('[data-page="outgoing"]').click();
 check(await page.locator('#outgoingTone').inputValue()==='none'&&await page.locator('#outgoingEnglishCase').inputValue()==='normal','old configuration shows non intrusive defaults');
 check(await page.locator('#outgoingCustomTone').isDisabled()&&!(await page.locator('#outgoingCustomToneFields').isVisible()),'custom prompt hidden unless selected');
 check(await page.locator('#outgoingTone option').count()===7,'none five presets and custom available');
 await page.locator('#outgoingTone').selectOption('custom');check(await page.locator('#outgoingCustomTone').isVisible()&&!(await page.locator('#outgoingCustomTone').isDisabled()),'custom mode opens textarea');
 const custom='<img src=x onerror="window.pwned=true"> 简短礼貌';await page.locator('#outgoingCustomTone').fill(custom);await page.locator('#outgoingEnglishCase').selectOption('upper');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 check(saved.outgoingTone==='custom'&&saved.outgoingCustomTone===custom&&saved.outgoingEnglishCase==='upper','settings save independent custom tone and case');
 check(await page.evaluate(()=>!window.pwned),'custom prompt is not rendered as HTML');
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="outgoing"]').click();check(await page.locator('#outgoingCustomTone').inputValue()===custom&&await page.locator('#outgoingEnglishCase').inputValue()==='upper','choices restored on page reload');
 await page.locator('#outgoingTargetLanguage').selectOption('ru');check(await page.locator('#outgoingEnglishCase').isDisabled()&&await page.locator('#outgoingEnglishCase').inputValue()==='upper','non English target disables case without discarding choice');
 await page.locator('#outgoingTargetLanguage').selectOption('custom');await page.locator('#outgoingCustomLanguage').fill('英语');await page.locator('#outgoingCustomLanguageCode').fill('en-US');check(!(await page.locator('#outgoingEnglishCase').isDisabled()),'custom English code enables case');
 await page.locator('#outgoingTone').selectOption('none');check(!(await page.locator('#outgoingCustomToneFields').isVisible()),'none removes custom prompt control from active UI');
 await page.locator('#outgoingTargetLanguage').selectOption('en');await page.locator('#outgoingTone').selectOption('polite');await page.locator('#outgoingEnglishCase').selectOption('lower');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));check(saved.outgoingTone==='polite'&&saved.outgoingEnglishCase==='lower','preset and lowercase save without overwriting custom draft');
 await page.locator('article').filter({has:page.locator('#outgoingTone')}).screenshot({path:path.join(root,'artifacts/outgoing-style-wide.png')});await page.setViewportSize({width:510,height:900});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'new controls fit narrow in game window');await page.locator('article').filter({has:page.locator('#outgoingTone')}).screenshot({path:path.join(root,'artifacts/outgoing-style-narrow.png')});
 check(errors.length===0,'no browser errors after reorganization');fs.writeFileSync(path.join(root,'artifacts/outgoing-style-ui-tests.json'),JSON.stringify({passed,realBackend:false}));console.log('TOTAL '+passed+' passed');
 }finally{await browser.close();server.close();}})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
