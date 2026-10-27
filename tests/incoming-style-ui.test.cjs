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
 await page.locator('[data-page="incoming"]').click();
 const owner=async id=>page.locator('#'+id).evaluate(el=>el.closest('section.page').id);
 check(await owner('incomingTone')==='incoming'&&await owner('incomingCustomTone')==='incoming','receive style belongs to dedicated receive page');
 check(await owner('incomingShowOwnTranslations')==='appearance','own display filtering belongs to appearance');
 check(await page.locator('#incomingTone').inputValue()==='none','old configuration defaults to no receive style');
 check(await page.locator('#incomingTone option').count()===7,'none five receive presets and custom available');
 check(await page.locator('#incomingCustomTone').isDisabled()&&!await page.locator('#incomingCustomToneFields').isVisible(),'custom receive field inactive by default');
 await page.locator('#incomingTone').selectOption('custom');
 check(await page.locator('#incomingCustomTone').isVisible()&&!await page.locator('#incomingCustomTone').isDisabled(),'custom receive mode reveals its own input');
 const custom='<img src=x onerror="window.pwned=true"> 短句，保留原意';
 await page.locator('#incomingCustomTone').fill(custom);await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 check(saved.incomingTone==='custom'&&saved.incomingCustomTone===custom&&saved.outgoingTone==='none','receive settings save without changing outgoing tone');
 check(await page.evaluate(()=>!window.pwned),'receive preference remains plain textarea data');
 await page.locator('[data-page="appearance"]').click();check(await page.locator('#incomingShowOwnTranslations').isChecked(),'legacy config shows own translations by default');
 await page.locator('#incomingShowOwnTranslations').uncheck();await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 check(saved.incomingShowOwnTranslations===false&&saved.outgoingEnabled===true,'own display filter saves independently of outgoing sending');
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 check(await page.locator('#incomingCustomTone').inputValue()===custom&&await page.locator('#incomingTone').inputValue()==='custom','receive style restored after reload');
 await page.locator('[data-page="appearance"]').click();check(!await page.locator('#incomingShowOwnTranslations').isChecked(),'own display filter restored after reload');
 await page.locator('[data-page="incoming"]').click();await page.locator('#mode').selectOption('fast');
 check((await page.locator('article').filter({has:page.locator('#incomingTone')}).innerText()).includes('快译不使用风格提示'),'fast-only behavior is explained without losing stored choice');
 await page.locator('#incomingTone').selectOption('none');check(!await page.locator('#incomingCustomToneFields').isVisible(),'none hides inactive custom preference');
 await page.locator('#incomingTone').selectOption('concise');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 check(saved.incomingTone==='concise'&&saved.incomingCustomTone===custom,'preset selection preserves inactive custom draft locally');
 await page.locator('article').filter({has:page.locator('#incomingTone')}).screenshot({path:path.join(root,'artifacts/incoming-style-wide.png')});
 await page.setViewportSize({width:510,height:900});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'receive options fit narrow game settings');
 await page.locator('article').filter({has:page.locator('#incomingTone')}).screenshot({path:path.join(root,'artifacts/incoming-style-narrow.png')});
 check(errors.length===0,'no browser errors after reorganization');fs.writeFileSync(path.join(root,'artifacts/incoming-style-ui-tests.json'),JSON.stringify({passed,realBackend:false}));console.log('TOTAL '+passed+' passed');
 }finally{await browser.close();server.close();}})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
