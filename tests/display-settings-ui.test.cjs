const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');let passed=0,saved;
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'quality',fastProvider:'bing',fontSize:17,scanIntervalMs:200,fastConcurrency:4,llmConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,transport:'fragment',llmThinkingEnabled:true};
const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
const server=http.createServer(async(req,res)=>{res.setHeader('Content-Type','application/json');const url=req.url.split('?')[0];
 if(url==='/api/config'&&req.method==='PUT'){let body='';for await(const c of req)body+=c;saved=JSON.parse(body);Object.assign(settings,saved);res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));}
 else if(url==='/api/config')res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));
 else if(url==='/api/terms')res.end('[]');else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:12,pending:0,connected:true},terms:10,gameEvidence:{metrics:{hudWaits:3,hudRetryRecovered:2,hudNativeExpired:1,feedRendered:4}},diagnostics:{phase:'connected',certificate:{state:'ready'}}}));
 else if(url.startsWith('/api/'))res.end('{}');
 else if(['/app.js','/style.css','/settings-navigation.js','/settings-background.js','/settings-background.css'].includes(url)){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1120,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/in-game/fixture');
 await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 check((await page.locator('#displayStatus').textContent()).includes('重试恢复 2'),'overview separates display waits recovery and retirement');
 check((await page.locator('#displayStatus').textContent()).includes('写入不等同于屏幕可见'),'diagnostics do not claim a successful pixel display');
 await page.locator('[data-page="appearance"]').click();
 check(await page.locator('#incomingDisplayMode').inputValue()==='native','old configs preserve native mode');
 check(!(await page.locator('#incomingFeedOptions').isVisible()),'independent duration controls hidden by default');
 await page.locator('#incomingDisplayMode').selectOption('independent');
 check(await page.locator('#incomingFeedOptions').isVisible(),'independent mode reveals bounded display options');
 check(await page.locator('#incomingFeedSeconds').inputValue()==='8'&&await page.locator('#incomingFeedLines').inputValue()==='3','old configs get safe lifetime and line defaults');
 await page.locator('#incomingFeedSeconds').fill('12');await page.locator('#incomingFeedLines').fill('4');await page.evaluate(()=>document.activeElement?.blur());await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
 check(saved.incomingDisplayMode==='independent'&&saved.incomingFeedSeconds===12&&saved.incomingFeedLines===4,'mode and numeric limits save with correct types');
 check(saved.mode==='quality'&&saved.llmThinkingEnabled===true,'display preferences preserve translation mode and thinking');
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="appearance"]').click();
 check(await page.locator('#incomingDisplayMode').inputValue()==='independent'&&await page.locator('#incomingFeedSeconds').inputValue()==='12','display settings restore after reload');
 await page.locator('#appearance').screenshot({path:path.join(root,'artifacts/display-settings-wide.png')});await page.setViewportSize({width:510,height:900});
 check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'controls fit a narrow settings window');
 await page.locator('#appearance').screenshot({path:path.join(root,'artifacts/display-settings-narrow.png')});
 await page.locator('[data-page="outgoing"]').click();check(!(await page.locator('#incomingDisplayMode').isVisible()),'incoming display remains outside sending settings');
 check(errors.length===0,'no browser errors');fs.writeFileSync(path.join(root,'artifacts/display-settings-ui-tests.json'),JSON.stringify({passed,realBackend:false,realGame:false}));console.log('TOTAL '+passed+' passed');
}finally{await browser.close();await new Promise(r=>server.close(r));}})().catch(e=>{console.error(e);process.exitCode=1;});
