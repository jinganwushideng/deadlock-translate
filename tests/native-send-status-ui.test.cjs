// Render the actual OutgoingService snapshots, without using live user data.
const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),folder=path.join(root,'artifacts/send-native-20261027');
const snapshots=JSON.parse(fs.readFileSync(path.join(folder,'visibility-snapshots.json'),'utf8'));
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'quality',fastProvider:'bing',outgoingTargetLanguage:'en',outgoingDisplayMode:'bilingual',outgoingMode:'auto',fontSize:17,scanIntervalMs:200,fastConcurrency:4,llmConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'',transport:'fragment'};
let items=[snapshots[0]],passed=0;const check=(value,name)=>{assert(value,name);passed++;console.log('PASS '+name);};
const server=http.createServer((req,res)=>{const url=req.url.split('?')[0];res.setHeader('Content-Type','application/json');
 if(url==='/api/config')res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));
 else if(url==='/api/terms')res.end('[]');
 else if(url==='/api/outgoing')res.end(JSON.stringify({items}));
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:0,diagnostics:{phase:'connected',certificate:{state:'ready'},sending:{state:'idle'},gamePrivileges:{detected:false}}}));
 else if(url.startsWith('/api/'))res.end('{}');
 else if(url==='/app.js'||url==='/style.css'){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else {res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1120,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="outgoing"]').click();
 await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('可找回的首条原文'));
 const waiting=await page.locator('#outgoingItems').innerText();check(waiting.includes('等待输入关闭')&&waiting.includes('已保留原文'),'registered task and retained original are visible while native close is pending');check(waiting.includes('复制原文')&&!waiting.includes('等待频道确认'),'waiting for input is distinguished from channel confirmation');
 await page.locator('#outgoingItems').screenshot({path:path.join(folder,'waiting-input-status.png')});
 items=[snapshots[1]];await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('已观察本机回显'));
 const observed=await page.locator('#outgoingItems').innerText();check(observed.includes('第 1 段：未观察到回显')&&observed.includes('第 2 段：已观察本机回显'),'each segment shows its own local observation status');check(observed.includes('已交给游戏')&&observed.includes('不代表服务器确认'),'dispatch and local echo are never described as server receipt');
 await page.locator('#outgoingItems').screenshot({path:path.join(folder,'part-observation-status.png')});
 items=[{...snapshots[1],id:'legacy-client',partStatuses:null}];await page.waitForFunction(()=>document.querySelector('[data-task-id="legacy-client"]'));
 const legacy=await page.locator('#outgoingItems').innerText();check(legacy.includes('已交给游戏')&&!legacy.includes('本机回显')&&!legacy.includes('未观察到回显'),'legacy client remains dispatch only without fabricated observations');check(errors.length===0,'native send task status renderer has no browser errors');
 fs.writeFileSync(path.join(folder,'status-ui-tests.json'),JSON.stringify({passed,total:passed,realGame:false,realBackendSnapshots:true,cloudCalls:0},null,2));console.log('TOTAL '+passed);
 }finally{await browser.close();server.close();}})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
