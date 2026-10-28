const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');let passed=0,scheduling={threshold:2,unfinished:1,burst:false,fastLimit:1,qualityLimit:1};
const settings={enabled:true,incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',fontSize:17,scanIntervalMs:200,fastConcurrency:4,llmConcurrency:4,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',outgoingConcurrency:3,outgoingQueueLimit:12,transport:'fragment'};
const check=(value,name)=>{assert(value,name);passed++;console.log('PASS '+name);};
const server=http.createServer((req,res)=>{res.setHeader('Content-Type','application/json');const url=req.url.split('?')[0];
 if(url==='/api/config')res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));
 else if(url==='/api/terms')res.end('[]');else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:3,pending:scheduling?.unfinished||0,connected:true,scheduling},requests:{fast:{active:1,limit:4,waiting:0},llm:{active:1,limit:4,waiting:0}},sending:{pending:2},terms:0,diagnostics:{phase:'connected',certificate:{state:'ready'},sending:{state:'idle'},gamePrivileges:{detected:false}}}));
 else if(url.startsWith('/api/'))res.end('{}');
 else if(['/app.js','/style.css','/settings-navigation.js','/settings-background.js','/settings-background.css'].includes(url)){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1120,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/in-game/fixture');
 await page.waitForFunction(()=>document.querySelector('#queueStatus').textContent.includes('接收逐条'));
 check((await page.locator('#queueStatus').textContent()).includes('未完成 1 / 触发 2'),'one pending message displays serial state and threshold');
 scheduling={...scheduling,unfinished:2,burst:true,fastLimit:4,qualityLimit:4};await page.waitForFunction(()=>document.querySelector('#queueStatus').textContent.includes('接收并发'));
 check((await page.locator('#queueStatus').textContent()).includes('未完成 2 / 触发 2'),'second pending message displays concurrent state without reloading');
 scheduling={...scheduling,unfinished:0,burst:false,fastLimit:1,qualityLimit:1};await page.waitForFunction(()=>document.querySelector('#queueStatus').textContent.includes('未完成 0 / 触发 2'));
 check((await page.locator('#queueStatus').textContent()).includes('接收逐条'),'draining returns displayed state to serial');
 check((await page.locator('#queueStatus').textContent()).includes('待发送 2'),'outgoing queue information remains independent');
 await page.locator('[data-page="providers"]').click();const hint=await page.locator('#receiveBurstHint').textContent();
 check(hint.includes('达到 2 条')&&hint.includes('不等待凑齐 2 条'),'service page explains threshold without collection delay');
 check(hint.includes('链路保持独立')&&hint.includes('不会被打断'),'service page explains independent pipelines and in-flight requests');
 check(await page.locator('#fastConcurrency').inputValue()==='4'&&await page.locator('#llmConcurrency').inputValue()==='4','threshold does not overwrite configured service caps');
 scheduling=undefined;await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 check((await page.locator('#queueStatus').textContent()).includes('快译并发'),'older backend without scheduling metadata remains usable');
 check(errors.length===0,'threshold status UI has no browser script errors');
 fs.writeFileSync(path.join(root,'artifacts/receive-burst-ui-tests.json'),JSON.stringify({passed,realBackend:false,gameTested:false},null,2));
}finally{await browser.close();await new Promise(r=>server.close(r));}})().catch(e=>{console.error(e);process.exitCode=1;});
