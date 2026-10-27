// Isolated real development backend + headless settings page + local fake provider.
// No game process, real credentials, trust-store writes or external HTTP requests.
const {chromium}=require('playwright'),http=require('node:http'),net=require('node:net'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),runtime=path.join(root,'src/Bridge/bin/Debug/net10.0-windows'),state=path.join(root,'artifacts','upgrade-0623-http-'+Date.now());
fs.mkdirSync(state,{recursive:true});const key=crypto.randomBytes(24).toString('hex'),pairing=path.join(state,'pairing.key');fs.writeFileSync(pairing,key);
const expectedVersion=JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version;
const requests=[];let passed=0,child,browser,base,headers,seq=0;
const sleep=ms=>new Promise(r=>setTimeout(r,ms)),check=(value,name)=>{assert(value,name);passed++;console.log('PASS '+name);};
const port=()=>new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
const provider=http.createServer(async(req,res)=>{let text='';for await(const chunk of req)text+=chunk;const body=JSON.parse(text);requests.push(body);res.setHeader('Content-Type','application/json');const output=body.messages[1].content.includes('LONG_TEST')?'x'.repeat(201):'fixture response';res.end(JSON.stringify({choices:[{finish_reason:'stop',message:{content:output}}]}));});
async function api(route,method='GET',body){const response=await fetch(base+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});const data=await response.json();return {status:response.status,data};}
async function exchange(extra={}){const response=await fetch(base+'/game/'+key,{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify({session:'fixture-session',seq:++seq,messages:[],poll:[],clientVersion:expectedVersion,multipartSupported:true,...extra})});assert.equal(response.status,200);return response.json();}
async function ready(id,extra={}){for(let i=0;i<80;i++){const reply=await exchange(extra);const job=reply.outgoing.find(j=>j.id===id);if(job?.state==='failed')throw Error(job.error);if(job?.state==='ready')return job;await sleep(25);}throw Error('Job not ready');}
(async()=>{try{
 await new Promise(r=>provider.listen(0,'127.0.0.1',r));const httpPort=await port(),httpsPort=await port();base='http://localhost:'+httpPort;
 const env={...process.env};delete env.__COMPAT_LAYER;
 child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pairing,'--port',String(httpPort),'--https-port',String(httpsPort)],{cwd:runtime,windowsHide:true,env,stdio:['ignore','pipe','pipe']});
 const log=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(log);child.stderr.pipe(log);
 let health;for(let i=0;i<100;i++){try{const response=await fetch(base+'/health');if(response.ok){health=await response.json();break;}}catch{}await sleep(100);}assert(health,'development backend started');
 check(health.version===expectedVersion&&health.buildId.startsWith(expectedVersion+'-')&&/^[a-f0-9]{12}$/.test(health.buildId.slice(expectedVersion.length+1)),'backend reports specific development build');
 const login=await fetch(base+'/in-game/'+key,{redirect:'manual'}),cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');assert(cookie);
 headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 let settings=(await api('config')).data.settings;
 settings={...settings,llmBaseUrl:'http://127.0.0.1:'+provider.address().port+'/v1',llmKey:'fixture-only-secret',model:'common-model',llmIndependentProfiles:true,incomingModel:'receive-model',outgoingModel:'send-model',incomingThinking:false,outgoingThinking:true,llmThinkingApi:'deepseek',llmStreaming:false,outgoingMode:'quality',outgoingSeparateBilingual:true,outgoingSplitLong:true,outgoingDisplayMode:'bilingual',quickMessages:[{id:'q-1',text:'PRIVATE_PHRASE_FIXTURE',hotkey:'Ctrl+F1',channel:'team',enabled:true}]};
 check((await api('config','PUT',settings)).status===200,'new settings accepted through real HTTP config');
 delete settings.llmKey;
 const config=(await api('config')).data;check(config.hasLlmKey&&config.settings.llmKey===null&&config.settings.quickMessages.length===1,'secret stays redacted while phrase remains editable');
 check((await api('config','PUT',{...settings,quickMessages:[...settings.quickMessages,{id:'q-2',text:'conflict',hotkey:'LCtrl+F1',channel:'all',enabled:true}]})).status===400,'conflicting shortcuts rejected through API');
 await api('test','POST',{direction:'incoming',provider:'quality',text:'INCOMING_PROFILE_TEST'});
 await api('test','POST',{direction:'outgoing',provider:'quality',text:'发送配置验证'});
 check(requests.length===2&&requests[0].model==='receive-model'&&requests[0].thinking.type==='disabled'&&requests[1].model==='send-model'&&requests[1].thinking.type==='enabled','real route respects direction-specific model and thinking');
 const before=requests.length,preview=await api('terms/preview','POST',{text:'蓝牛出个跑酷',direction:'outgoing',provider:'quality',nameStyle:'current',bilingual:true});
 check(preview.status===200&&preview.data.localOnly&&preview.data.matches.length>0&&requests.length===before,'glossary preview stays local');
 check((await fetch(base+'/api/terms/preview',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status===401,'preview retains settings authentication');
 await exchange({clientBuildId:health.buildId,outgoing:[{id:'parts',text:'LONG_TEST',channel:'team',displayMode:'bilingual'}]});let job=await ready('parts');
 check(job.partCount===4&&job.partIndex===0&&job.sendText==='LONG_TEST','bilingual first part followed by three long translation parts');
 check(job.sendCharacters===9&&job.sendCharacterLimit===80,'HTTP response exposes native character count and limit');
 for(let i=0;i<4;i++){
  const reply=await exchange({outgoingPartAcks:[{id:'parts',part:i}]});const next=reply.outgoing.find(j=>j.id==='parts');
  check(i===3?!next:next.partIndex===i+1&&Array.from(next.sendText).length<=80&&next.sendCharacters<=80,'HTTP part acknowledgement '+i+' advances exactly one native-sized part');
 }
 await exchange({outgoing:[{id:'old-client',text:'different LONG_TEST',channel:'all',displayMode:'bilingual'}],multipartSupported:false});
 let old;for(let i=0;i<60;i++){old=(await exchange()).outgoing.find(j=>j.id==='old-client');if(old?.state==='failed')break;await sleep(25);}check(old?.state==='failed'&&old.error.includes('VPK'),'old game script cannot send only the first fragment');
 await api('config','PUT',{...settings,outgoingSeparateBilingual:false,outgoingSplitLong:false});
 await exchange({outgoing:[{id:'clipped',text:'LONG_TEST_CAP',channel:'party',displayMode:'bilingual'}]});const clipped=await ready('clipped');
 check(clipped.partCount===1&&clipped.sendCharacters===80&&clipped.truncated&&clipped.omittedCharacters===137&&clipped.english.length===201,'splitting off submits maximum prefix while retaining full translation and omitted count');
 await exchange({outgoingSent:['clipped']});
 check((await api('outgoing')).data.items.find(j=>j.id==='clipped').truncated,'submitted task retains the explicit clipping notice');
 await api('config','PUT',settings);
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1120,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 await page.locator('[data-page="outgoing"]').click();await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('41/80 字符'));
 check(true,'settings shows the current part character count against actual game limit');
 check((await page.locator('.outgoing-task[data-task-id="clipped"]').innerText()).includes('137 个字符未发送'),'settings distinguishes intentional single-part clipping from a failed send');
 check(await page.locator('#incomingModel').inputValue()==='receive-model'&&await page.locator('#incomingThinking').inputValue()==='false','settings displays saved receive profile');
 await page.locator('[data-page="outgoing"]').click();check(await page.locator('#outgoingSeparateBilingual').isChecked(),'multipart switches visible in sending');await page.locator('[data-page="shortcuts"]').click();check(await page.locator('#quickMessageRows textarea').count()===1,'saved phrase visible in shortcuts');
 await page.locator('#quickMessageRows textarea').first().fill('<img src=x onerror=window.hacked=true> preset');
 await page.locator('#quickMessageRows button').filter({hasText:'录制'}).click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('快捷键监听未启用'));
 check(true,'quick phrase recording has actionable listener-unavailable message');
 await page.locator('#quickMessageRows textarea').first().fill('PRIVATE_PHRASE_FIXTURE');
 for(let i=1;i<10;i++)await page.locator('#addQuickMessage').click();
 check(await page.locator('#addQuickMessage').isDisabled()&&await page.locator('#quickMessageRows textarea').count()===10,'settings limits phrase editor to ten rows');
 for(let i=9;i>=1;i--)await page.locator('#quickMessageRows .quick-message-row').nth(i).getByRole('button',{name:'删除'}).click();
 await page.locator('[data-page="providers"]').click();await page.locator('#modelTab-outgoing').click();await page.locator('#outgoingModel').fill('other-send-model');await page.locator('#outgoingThinking').selectOption('');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 const saved=(await api('config')).data.settings;check(saved.outgoingModel==='other-send-model'&&saved.outgoingThinking===null&&saved.incomingThinking===false,'profile saving preserves null inheritance and the other direction');
 await page.locator('[data-page="providers"]').click();await page.locator('#modelTab-incoming').click();
 // Reuse existing connection test control; its request must use the selected profile draft.
 await page.locator('#testLlmConfig').click();await page.waitForFunction(()=>document.querySelector('#llmApiReport').textContent.includes('API 连接测试通过'));check(requests.at(-1).model==='receive-model'&&requests.at(-1).thinking.type==='disabled','API diagnostic tests selected receive profile');
 await page.locator('[data-page="glossary"]').click();await page.locator('#termPreviewText').fill('蓝牛出个跑酷');await page.locator('#termPreviewDirection').selectOption('outgoing');await page.locator('#previewTerms').click();await page.waitForFunction(()=>document.querySelector('#termPreviewResult').textContent.includes('专名处理后'));
 check((await page.locator('#termPreviewResult').innerText()).includes('Abrams'),'preview renders canonical game name');
 await page.locator('#termPreviewResult button').first().click();check((await page.locator('#termSearch').inputValue()).length>0,'preview match locates editable glossary row');
 await page.locator('[data-page="shortcuts"]').click();await page.locator('#quickMessageRows').scrollIntoViewIfNeeded();await page.locator('article').filter({has:page.locator('#quickMessageRows')}).screenshot({path:path.join(state,'settings-wide.png')});
 await page.setViewportSize({width:510,height:900});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'new settings fit narrow in-game window');await page.locator('article').filter({has:page.locator('#quickMessageRows')}).screenshot({path:path.join(state,'settings-narrow.png')});
 check(errors.length===0&&await page.evaluate(()=>!window.hacked),'no page errors or markup execution');
 const diagnostic=await api('diagnostics/export','POST',{});check(diagnostic.status===200,'diagnostic export works with new settings');
 fs.writeFileSync(path.join(state,'result.json'),JSON.stringify({passed,cloudUsed:false,gameTested:false,installerBuilt:false,buildId:health.buildId,diagnostic:diagnostic.data.file},null,2));
 console.log(JSON.stringify({passed,artifacts:state}));
 }finally{if(browser)await browser.close();if(child){try{await api('stop','POST',{});}catch{}await sleep(500);if(child.exitCode===null)child.kill();}provider.close();}})().catch(error=>{console.error(error);process.exitCode=1;});
