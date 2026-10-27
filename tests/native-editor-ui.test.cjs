const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');let passed=0,saves=0,termSaves=0,serial=0,savedTerms;
const jobs=new Map(),requests=[],errors=[];
let terms=[{source:'Silver',target:'希尔芙',legacyTarget:'',english:'Silver',kind:'name',ambiguous:false}];
let settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',bilingualNames:true,fontSize:17,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'',outgoingMode:'quality',outgoingDisplayMode:'translation',outgoingFallbackFast:false,reserveSendSlot:true,transport:'fragment'};
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://fixture.invalid');let body='';for await(const c of req)body+=c;
 if(url.pathname.startsWith('/api/')){
  res.setHeader('Content-Type','application/json');const input=body?JSON.parse(body):null;
  if(url.pathname==='/api/config'){if(req.method==='PUT'){saves++;settings=input;}return res.end(JSON.stringify({settings,hasLlmKey:false,hasAzureKey:false}));}
  if(url.pathname==='/api/terms'){if(req.method==='PUT'){termSaves++;savedTerms=input;terms=input;}return res.end(JSON.stringify(terms));}
  if(url.pathname==='/api/native-editor'&&req.method==='POST'){
   const id=(++serial).toString(16).padStart(48,'0');assert.match(input.owner,/^[a-f0-9]{32}$/);requests.push(input);jobs.set(id,{...input,id,state:'pending'});return res.end(JSON.stringify({id,state:'pending'}));
  }
  if(url.pathname.startsWith('/api/native-editor/')){
   const job=jobs.get(url.pathname.split('/').pop());if(!job||job.owner!==url.searchParams.get('owner')){res.statusCode=404;return res.end('{"error":"expired"}');}
   if(req.method==='DELETE'){job.state='cancelled';job.value='';return res.end('{}');}
   return res.end(JSON.stringify({id:job.id,state:job.state,...job.state==='applied'?{value:job.value}:{}}));
  }
  if(url.pathname==='/api/status')return res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:1,sending:{pending:0},diagnostics:{phase:'connected',version:'0.6.24',certificate:{state:'ready'},hotkey:{available:true},sending:{state:'idle'}}}));
  if(url.pathname==='/api/outgoing')return res.end('{"items":[],"hotkeyAvailable":true}');
  return res.end('{}');
 }
 const asset=url.pathname.slice(1),name=['style.css','app.js','settings-background.css','settings-background.js','settings-background-v3-flat.png','settings-navigation.js'].includes(asset)?asset:'index.html';res.setHeader('Content-Type',name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',name)));
});
function check(ok,name){if(!ok&&errors.length)console.error(errors);assert(ok,name);passed++;console.log('PASS '+name);}
const latest=()=>[...jobs.values()].at(-1);
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({channel:'msedge',headless:true}),page=await browser.newPage({viewport:{width:1100,height:850}});page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto(base+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  await page.locator('[data-page="diagnostics"]').click();await page.waitForSelector('#diagnostics:visible');check(await page.locator('#nativeInputHelp').isVisible(),'game page offers a visible native compatibility mode');
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingTone').selectOption('custom');
  await page.locator('#incomingCustomTone').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  check(requests.length===1&&latest().value===''&&latest().multiline===true,'click creates a page-owned multiline draft without typing in CEF');
  const initialGeometry=await page.evaluate(()=>{const v=document.title.split(':').at(-1).split(',').map(Number),r=document.querySelector('#incomingCustomTone').getBoundingClientRect();return {v,x:Math.round(r.left),y:Math.round(r.top),w:Math.round(r.width),h:Math.round(r.height)};});
  check(initialGeometry.v.length===16&&initialGeometry.v[2]===initialGeometry.x&&initialGeometry.v[3]===initialGeometry.y&&initialGeometry.v[4]===initialGeometry.w&&initialGeometry.v[5]===initialGeometry.h,'inline title contains bounded numeric geometry for the exact DOM field');
  const oldTitle=await page.title();await page.evaluate(()=>document.querySelector('#contentScroll').scrollTop-=25);await page.waitForFunction(title=>document.title!==title,oldTitle);check((await page.title()).split(':').at(-1)!==oldTitle.split(':').at(-1),'scrolling immediately republishes the field position');
  latest().state='editing';await page.waitForFunction(()=>document.querySelector('#nativeInputStatus').textContent.includes('正在原位置编辑'));
  latest().value='用中文玩家口语\n保留角色与装备名';latest().state='applied';await page.waitForFunction(()=>document.querySelector('#incomingCustomTone').value==='用中文玩家口语\n保留角色与装备名');
  check(await page.locator('#incomingCustomTone').inputValue()==='用中文玩家口语\n保留角色与装备名','confirmed native Unicode reaches the actual field before automatic persistence');
  await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');check(settings.incomingCustomTone==='用中文玩家口语\n保留角色与装备名'&&saves>=1,'automatic save persists only the returned Unicode draft');
  await page.locator('#incomingCustomTone').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));const cancelled=latest();await page.locator('#cancelNativeInput').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  cancelled.state='applied';cancelled.value='迟到内容';await page.waitForTimeout(500);check(await page.locator('#incomingCustomTone').inputValue()==='用中文玩家口语\n保留角色与装备名','cancelled or late results never overwrite the original field');
  await page.locator('#incomingCustomTone').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));await page.locator('#incomingTone').selectOption('none');
  await page.waitForFunction(()=>{const parts=document.title.split(':'),g=parts.at(-1).split(',').map(Number);return parts[4]==='1'&&g[4]>0&&g[5]>0&&g[8]===g[6];});
  check(await page.locator('#incomingCustomTone').isDisabled(),'hiding a custom field requests a final native flush with clipped geometry');
  latest().state='applied';latest().value='切换风格前输入的草稿';await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));check(await page.locator('#incomingCustomTone').inputValue()==='切换风格前输入的草稿','disabling a former custom field retains its final draft');
  await page.locator('#incomingTone').selectOption('custom');
  await page.locator('[data-page="glossary"]').click();const source=page.getByRole('textbox',{name:'原词',exact:true}).first();await source.click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  check(requests.at(-1).label==='原词'&&requests.at(-1).maxLength===100,'dynamic glossary rows use the same editor and their actual bounds');
  await page.evaluate(()=>{document.querySelector('.table-wrap').style.width='260px';});
  await page.waitForFunction(()=>{const g=document.title.split(':').at(-1).split(',').map(Number);return g[8]-g[6]<=260;});
  check(await page.evaluate(()=>{const g=document.title.split(':').at(-1).split(',').map(Number),r=document.querySelector('.table-wrap').getBoundingClientRect();return g[6]>=Math.round(r.left)&&g[8]<=Math.round(r.right); }),'native geometry clips to the nested glossary table rather than the whole webpage');
  const tableX=Number((await page.title()).split(':').at(-1).split(',')[2]);await page.evaluate(()=>document.querySelector('.table-wrap').scrollLeft=30);
  await page.waitForFunction(x=>Number(document.title.split(':').at(-1).split(',')[2])<x,tableX);check(Number((await page.title()).split(':').at(-1).split(',')[2])===tableX-30,'nested horizontal scrolling updates the native field anchor');
  await page.setViewportSize({width:1250,height:850});await page.waitForFunction(()=>Number(document.title.split(':').at(-1).split(',')[0])===1250);check(latest().value==='Silver','viewport resize updates geometry without replacing the pending text');
  await page.setViewportSize({width:1100,height:850});await page.evaluate(()=>{const wrap=document.querySelector('.table-wrap');wrap.style.width='';wrap.scrollLeft=0;});
  latest().state='applied';latest().value='狼女';await page.waitForFunction(()=>document.querySelector('#termRows input').value==='狼女');check(termSaves===0,'native term editing does not auto-save the glossary');
  await page.locator('#saveTerms').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('词库已保存'));check(savedTerms[0].source==='狼女'&&termSaves===1,'normal glossary input handler retains the returned value');
  await source.click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));await page.evaluate(()=>{const q=document.querySelector('#termSearch');q.value='no-match';q.dispatchEvent(new Event('input'));});latest().state='applied';latest().value='不该覆盖';
  await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('原输入框已变化'));check(await page.locator('#termRows tr').count()===0,'detached native result cannot be written into a replacement field');
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingCustomTone').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  await page.evaluate(()=>{const f=document.querySelector('#incomingCustomTone');f.value='其他编辑产生的新草稿';f.dispatchEvent(new Event('input'));});latest().state='applied';latest().value='旧结果';
  await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));check(await page.locator('#incomingCustomTone').inputValue()==='其他编辑产生的新草稿','concurrent field changes are preserved');
  await page.locator('[data-page="providers"]').click();await page.locator('#model').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  const savesBefore=saves;await page.locator('#pageTitle').click();await page.waitForFunction(()=>/^DLTSETTINGS:edit:[a-f0-9]{48}:\d+:1:/.test(document.title));check(saves===savesBefore,'save requests a native flush and does not persist the stale webpage value');
  latest().state='applied';latest().value='最后输入的模型';await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');check(saves>savesBefore&&settings.model==='最后输入的模型','automatic save waits for final native text before persisting');
  await page.locator('#model').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));await page.locator('[data-page="incoming"]').click();check(await page.locator('#pageTitle').textContent()==='翻译服务','navigation waits while a native field is still returning');
  latest().state='applied';latest().value='切页前的草稿';await page.waitForFunction(()=>document.querySelector('#pageTitle').textContent==='接收翻译');check(await page.locator('#model').inputValue()==='切页前的草稿','navigation retains the final draft without saving settings');
  await page.locator('[data-page="providers"]').click();await page.locator('#model').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));const first=latest(),count=requests.length;
  await page.locator('#llmBaseUrl').click();await page.waitForFunction(()=>/^DLTSETTINGS:edit:[a-f0-9]{48}:\d+:1:/.test(document.title));check(requests.length===count,'switching fields does not start overlapping native drafts');
  first.state='applied';first.value='连续编辑模型';await page.waitForFunction(id=>document.title.startsWith('DLTSETTINGS:edit:')&&!document.title.includes(id),first.id);check(latest().id!==first.id&&await page.locator('#model').inputValue()==='连续编辑模型','next field opens after the previous draft is safely returned');
  await page.locator('#cancelNativeInput').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));

  await page.locator('#model').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  const clickOnly=latest();await page.evaluate(()=>document.querySelector('#pageTitle').dispatchEvent(new MouseEvent('click',{bubbles:true})));
  await page.waitForFunction(()=>/^DLTSETTINGS:edit:[a-f0-9]{48}:\d+:1:/.test(document.title));check(true,'a click delivered without mouse-down still requests native text return');
  clickOnly.state='applied';clickOnly.value='点击回填测试';await page.waitForFunction(()=>document.querySelector('#model').value==='点击回填测试');
  await page.evaluate(()=>document.querySelector('#model').focus());await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:edit:'));
  check(latest().value==='点击回填测试','label or keyboard focus can start native editing without a direct mouse-down');
  const racing=latest(),racingCount=requests.length;
  await page.locator('#llmBaseUrl').click();await page.locator('#azureRegion').evaluate(input=>input.dispatchEvent(new MouseEvent('mousedown',{button:0,bubbles:true})));
  racing.state='applied';racing.value='保留首框最终内容';await page.waitForFunction(n=>document.querySelector('#model').value==='保留首框最终内容'&&document.title.startsWith('DLTSETTINGS:edit:')&&n!==document.title.split(':')[2],racing.id);
  check(requests.length===racingCount+1&&latest().value==='','rapid field switches claim only the latest requested field');
  await page.locator('#cancelNativeInput').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  const before=requests.length;await page.locator('#llmKey').click();await page.keyboard.type('fixture-key');check(requests.length===before,'password fields do not send secrets through a native title channel');
  await page.locator('[data-page="diagnostics"]').click();await page.locator('#nativeInputEnabled').uncheck();await page.locator('[data-page="providers"]').click();await page.locator('#model').click();await page.keyboard.type('-direct');check(requests.length===before,'compatibility mode can be disabled for direct English input');
  await page.locator('#pageTitle').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');await page.goto(base+'/');await page.waitForFunction(()=>document.querySelector('#connection').textContent==='后台已连接');check(await page.locator('#nativeInputHelp').isHidden(),'ordinary browser settings retain direct text entry');
  await page.locator('[data-page="incoming"]').click();await page.locator('#incomingTone').selectOption('custom');await page.locator('#incomingCustomTone').fill('browser direct text');check(requests.length===before,'browser edits never request a game-native editor');
  check(errors.length===0,'native editing frontend has no browser errors');
  fs.writeFileSync(path.join(root,'artifacts/native-editor-ui-tests.json'),JSON.stringify({passed,saves,termSaves,requests:requests.length,realGame:false,cloudCalls:0},null,2));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
