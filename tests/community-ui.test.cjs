// Local fixtures only: no real clipboard changes, browser launches or cloud calls.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),http=require('node:http'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'artifacts/community-settings-build'),evidence=path.join(root,'artifacts','community-ui-'+Date.now());fs.mkdirSync(evidence,{recursive:true});
let passed=0,browser,child,base,headers,profileCalls=0,saves=0;const sleep=ms=>new Promise(r=>setTimeout(r,ms)),check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);},sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const settings={mode:'quality',model:'fixture-model',llmBaseUrl:'https://fixture.invalid/v1',fontSize:17,scanIntervalMs:200,outgoingHotkey:'LAlt+Space',outgoingEnabled:true,incomingTargetLanguage:'zh-Hans',outgoingTargetLanguage:'en',incomingScope:'all_players'};
const server=http.createServer(async(req,res)=>{
 let body='';for await(const c of req)body+=c;const url=req.url.split('?')[0];res.setHeader('Content-Type','application/json');
 if(url==='/api/config'){if(req.method==='PUT')saves++;res.end(JSON.stringify({settings,hasLlmKey:false,hasAzureKey:false}));}
 else if(url==='/api/terms')res.end('[]');else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url==='/api/status')res.end('{"engine":{"messages":0,"pending":0,"connected":true},"terms":0}');
 else if(url==='/api/community/bilibili'){profileCalls++;res.end('{"opened":true}');}
 else if(url==='/app.js'||url==='/style.css'||url==='/community-qq.png'){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':url.endsWith('.css')?'text/css':'image/png');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
const port=()=>new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
async function api(name,method='GET',body){const r=await fetch(base+'/api/'+name,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});return{status:r.status,data:await r.json()};}
(async()=>{try{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const fixture='http://127.0.0.1:'+server.address().port;
 browser=await chromium.launch({channel:'msedge',headless:true});const context=await browser.newContext({viewport:{width:1280,height:1000}});
 await context.addInitScript(()=>{window.__copies=[];window.__denyCopy=false;Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async value=>{if(window.__denyCopy)throw Error('fixture clipboard blocked');window.__copies.push(value);}}});const original=document.execCommand.bind(document);document.execCommand=(...args)=>window.__denyCopy?false:original(...args);});
 const page=await context.newPage(),errors=[],external=[];page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>{if(!r.url().startsWith(fixture+'/'))external.push(r.url());});
 await page.goto(fixture+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 check((await page.locator('#overview > article').allTextContents()).map(t=>t.slice(0,10)).join('|').includes('反馈、更新与作者'),'community card belongs to overview');
 check(await page.evaluate(()=>{const a=[...document.querySelectorAll('#overview > article')];return a[1].id==='communityCard'&&a[2].textContent.includes('常用入口');}),'formal release community and navigation order is preserved');
 check(await page.locator('#communityGroup').innerText()==='923443423'&&await page.locator('#communityAuthor').innerText()==='ともり_一生','group and author match supplied identifiers');
 check(profileCalls===0&&external.length===0,'opening settings does not navigate to Bilibili or issue external requests');
 const before=await page.locator('#sidebarSaveState').textContent();await page.locator('#copyCommunityGroup').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('已复制 QQ 群号'));check(await page.evaluate(()=>window.__copies.at(-1))==='923443423','game settings can copy exact group number');
 await page.locator('#copyCommunityAuthor').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('已复制 B 站昵称'));check(await page.evaluate(()=>window.__copies.at(-1))==='ともり_一生','author nickname copies correctly');
 await page.locator('#copyCommunityAuthorUrl').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('已复制作者'));check(await page.evaluate(()=>window.__copies.at(-1))==='https://space.bilibili.com/332089385','copy uses the exact user-provided profile URL');
 await page.locator('#openCommunityAuthor').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('系统默认浏览器'));check(profileCalls===1&&page.url()===fixture+'/in-game/fixture','explicit open button requests the backend without replacing game settings');
 check(await page.locator('#sidebarSaveState').textContent()===before&&saves===0,'community actions do not alter settings or require Save');
 await page.locator('#communityCard').screenshot({path:path.join(evidence,'community-wide.png')});
 await page.locator('.community-qr summary').click();await page.waitForFunction(()=>{const i=document.querySelector('.community-qr img');return i.complete&&i.naturalWidth>0;});check(await page.locator('.community-qr-viewport').isVisible(),'offline QR expands on demand');
 const bounds=await page.locator('.community-qr-viewport').boundingBox();check(bounds.width===208&&bounds.height===208,'QR uses a compact square viewport without changing original pixels');
 await page.locator('#communityCard').screenshot({path:path.join(evidence,'community-qr.png')});
 await page.evaluate(()=>window.__denyCopy=true);await page.locator('#copyCommunityGroup').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('无法自动复制'));check((await page.locator('#communityStatus').innerText()).includes('923443423')&&await page.locator('#copyCommunityGroup').isEnabled(),'blocked clipboard gives an actionable fallback and restores button state');
 await page.evaluate(()=>window.__denyCopy=false);await page.setViewportSize({width:510,height:900});await page.locator('#communityCard').scrollIntoViewIfNeeded();check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'community and QR fit narrow game settings');await page.locator('#communityCard').screenshot({path:path.join(evidence,'community-narrow.png')});
 await page.goto(fixture+'/');await page.waitForFunction(()=>document.querySelector('#model').value==='fixture-model');await page.locator('#copyCommunityAuthorUrl').click();await page.waitForFunction(()=>document.querySelector('#communityStatus').textContent.includes('已复制作者'));check(await page.evaluate(()=>window.__copies.at(-1))==='https://space.bilibili.com/332089385','standalone browser settings support the same contact actions');
 check(errors.length===0&&external.length===0,'new controls cause no script errors or automatic external traffic');
 const state=path.join(evidence,'backend');fs.mkdirSync(state);const key=crypto.randomBytes(24).toString('hex'),pair=path.join(state,'pairing.key');fs.writeFileSync(pair,key);base='http://localhost:'+await port();const tls=await port();
 child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pair,'--port',new URL(base).port,'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});const log=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(log);child.stderr.pipe(log);
 let health;for(let i=0;i<150;i++){try{const r=await fetch(base+'/health');if(r.ok){health=await r.json();break;}}catch{}await sleep(100);}check(health?.buildId===JSON.parse(fs.readFileSync(path.join(root,'build-info.json'),'utf8')).buildId,'real backend build identity matches source');
 check((await fetch(base+'/api/community/bilibili',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status===401,'external browser action requires settings authentication');
 const login=await fetch(base+'/in-game/'+key),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 check((await fetch(base+'/api/community/bilibili',{method:'POST',headers:{...headers,Origin:'https://untrusted.example'},body:'{}'})).status===403,'external browser action rejects a foreign Origin');
 const opened=await api('community/bilibili','POST',{});check(opened.status===409&&opened.data.error.includes('复制主页链接'),'no-shell fixture cannot launch a real browser and gives copy-link guidance');
 const qr=await fetch(base+'/community-qq.png');check(qr.ok&&qr.headers.get('content-type').includes('image/png')&&sha(Buffer.from(await qr.arrayBuffer()))===sha(fs.readFileSync(path.join(root,'docs/images/qq-feedback-923443423.png'))),'backend serves the exact original QR from the local package');
 const config=await api('config');check(!config.data.settings.llmKeepAliveEnabled,'community links do not enable translation warmups or modify configuration');
 fs.writeFileSync(path.join(evidence,'result.json'),JSON.stringify({passed,buildId:health.buildId,gameTested:false,realClipboardChanged:false,realBrowserOpened:false,externalNetworkUsed:false},null,2));console.log(JSON.stringify({passed,artifacts:evidence}));
 }finally{if(browser)await browser.close();server.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(500);if(child.exitCode===null)child.kill();}}
})().catch(e=>{console.error(e);process.exitCode=1;});
