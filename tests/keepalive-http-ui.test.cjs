// Real local backend and settings page; only a local fixture model, never a paid API.
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),net=require('node:net'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'src/Bridge/bin/Debug/net10.0-windows'),state=path.join(root,'artifacts','keepalive-http-'+Date.now());fs.mkdirSync(state,{recursive:true});
const pairing=path.join(state,'pairing.key'),key=crypto.randomBytes(24).toString('hex');fs.writeFileSync(pairing,key);
let passed=0,child,browser,base,headers;const requests=[],sleep=ms=>new Promise(r=>setTimeout(r,ms));const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
const port=()=>new Promise(resolve=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const value=s.address().port;s.close(()=>resolve(value));});});
const provider=http.createServer(async(req,res)=>{let body='';for await(const c of req)body+=c;requests.push(JSON.parse(body));res.setHeader('Content-Type','application/json');res.end('{"choices":[{"message":{"content":"OK"},"finish_reason":"stop"}]}');});
async function api(name,method='GET',value){const r=await fetch(base+'/api/'+name,{method,headers,body:value===undefined?undefined:JSON.stringify(value)});return {status:r.status,data:await r.json()};}
(async()=>{try{
 await new Promise(r=>provider.listen(0,'127.0.0.1',r));const p=await port(),tls=await port();base='http://localhost:'+p;
 child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pairing,'--port',String(p),'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});const output=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(output);child.stderr.pipe(output);
 for(let i=0;i<100;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 const login=await fetch(base+'/in-game/'+key),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 let s=(await api('config')).data.settings;check(!s.llmKeepAliveEnabled&&s.llmKeepAliveSeconds===120,'old configuration receives disabled keepalive and 120s default');
 check((await fetch(base+'/api/llm/keep-alive',{method:'POST'})).status===401,'warmup remains behind settings credentials');
 check((await fetch(base+'/api/llm/keep-alive',{method:'POST',headers:{Cookie:cookie,'Content-Type':'application/json'},body:'{}'})).status===403,'cross-origin warmup cannot generate paid requests');
 await api('config','PUT',{...s,llmBaseUrl:'http://127.0.0.1:'+provider.address().port+'/v1',model:'fixture-model',llmKey:'fixture-key',llmThinkingEnabled:true,llmThinkingApi:'deepseek',llmStreaming:false,llmKeepAliveEnabled:true});
 await sleep(5500);check(requests.length===0,'automatic maintenance pauses with no game connection');
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1120,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="providers"]').click();
 check(await page.locator('#llmKeepAliveEnabled').isChecked(),'keepalive controls live in translation services');await page.locator('#llmKeepAliveSeconds').fill('300');await page.locator('#llmKeepAliveEnabled').uncheck();await page.locator('#saveSidebar').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 s=(await api('config')).data.settings;check(!s.llmKeepAliveEnabled&&s.llmKeepAliveSeconds===300&&s.llmThinkingEnabled,'custom interval saves without changing thinking');
 await page.locator('#llmKeepAliveNow').click();await page.waitForFunction(()=>document.querySelector('#llmKeepAliveStatus').textContent.includes('成功 1'));
 check(requests.length===1&&requests[0].max_tokens===16&&requests[0].thinking.type==='disabled','manual warmup sends one minimal request while timer is disabled');
 check(!requests[0].messages.some(m=>m.content.includes('聊天')),'warmup has no chat context');
 await page.locator('#llmKeepAliveNow').click();await sleep(150);check(requests.length===1,'rapid repeat clicks cannot send duplicate probes');
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="providers"]').click();check(await page.locator('#llmKeepAliveSeconds').inputValue()==='300'&&!await page.locator('#llmKeepAliveEnabled').isChecked(),'keepalive options survive page reload');
 check((await api('config','PUT',{...s,llmKeepAliveSeconds:30})).status===400,'API rejects an excessive request frequency');
 await page.setViewportSize({width:640,height:760});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'new service card fits narrow game settings');
 await page.screenshot({path:path.join(state,'keepalive-settings.png'),fullPage:true});check(errors.length===0,'no browser errors from new controls');
 const status=(await api('status')).data;check(status.diagnostics.keepAlive.requests===1,'keepalive has its own diagnostic counters');
 fs.writeFileSync(path.join(state,'result.json'),JSON.stringify({passed,cloudUsed:false,gameTested:false},null,2));console.log(JSON.stringify({passed,artifacts:state}));
 }finally{if(browser)await browser.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(400);if(child.exitCode===null)child.kill();}provider.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
