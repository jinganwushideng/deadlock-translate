const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version;
let passed=0,savedConfig,savedTerms,settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',bilingualNames:true,fontSize:17,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'',outgoingMode:'auto',outgoingDisplayMode:'translation',outgoingFallbackFast:false,reserveSendSlot:true,transport:'fragment'};
let terms=[{source:'Lash',target:'劳什',legacyTarget:'神鞭',english:'Lash',kind:'name',ambiguous:true},{source:'狼女',target:'西尔芙',english:'Silver',kind:'name',ambiguous:false}];
const diagnostics={version,clientVersion:version,phase:'connected',backendElevated:false,certificate:{state:'ready',message:'有效证书'},gamePrivileges:{detected:true,elevated:false,sameUser:true},sending:{state:'idle'},hotkey:{available:true,label:'左 Alt + 空格',triggered:0,acknowledged:0,ackTimeouts:0}};
function check(value,name){assert(value,name);passed++;console.log('PASS '+name);}
const server=http.createServer(async(req,res)=>{
 const url=req.url.split('?')[0];
 if(url.startsWith('/api/')){
  res.setHeader('Content-Type','application/json');let body='';for await(const c of req)body+=c;
  if(url==='/api/config'){if(req.method==='PUT'){savedConfig=JSON.parse(body);settings=savedConfig;}res.end(JSON.stringify({settings,hasLlmKey:false,hasAzureKey:false}));}
  else if(url==='/api/terms'){if(req.method==='PUT'){savedTerms=JSON.parse(body);terms=savedTerms;}res.end(JSON.stringify(terms));}
  else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:terms.length,sending:{pending:0},diagnostics}));
  else if(url==='/api/outgoing')res.end(JSON.stringify({items:[],hotkey:diagnostics.hotkey,hotkeyAvailable:true}));
  else res.end('{}');return;
 }
 const name=url==='/style.css'?'style.css':url==='/app.js'?'app.js':'index.html';res.setHeader('Content-Type',name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',name)));
});
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({channel:'msedge',headless:true}),page=await browser.newPage({viewport:{width:1100,height:850}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  await page.goto(base+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  await page.locator('[data-page="glossary"]').click();check(await page.locator('#chineseNameStyle').inputValue()==='current','old config defaults to current display');
  check((await page.locator('#nameStylePreview').innerText()).includes('劳什（Lash）'),'current sample uses current name and optional English');
  await page.locator('#chineseNameStyle').selectOption('legacy');check((await page.locator('#nameStylePreview').innerText()).includes('神鞭（Lash）'),'legacy selection changes sample immediately');
  await page.locator('#bilingualNames').uncheck();check(!(await page.locator('#nameStylePreview').innerText()).includes('（Lash）'),'English annotation remains independent');
  await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  check(savedConfig.chineseNameStyle==='legacy'&&savedConfig.bilingualNames===false,'save sends the selected naming style');
  check(savedConfig.model==='fixture'&&savedConfig.outgoingHotkey==='LAlt+Space','display changes preserve unrelated service and shortcut choices');
  await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="glossary"]').click();
  check(await page.locator('#chineseNameStyle').inputValue()==='legacy','selected display style survives page reload');
  check((await page.locator('#glossary').innerText()).includes('仅影响接收简体中文译文'),'setting describes language and sending limits');
  await page.screenshot({path:path.join(root,'artifacts/legacy-name-appearance.png'),fullPage:true});
  await page.locator('[data-page="glossary"]').click();await page.locator('#termSearch').fill('神鞭');check(await page.locator('#termRows tr').count()===1,'search finds old name stored on an English source entry');
  check(await page.getByRole('textbox',{name:'旧中文（可选）',exact:true}).inputValue()==='神鞭','legacy column shows stored old name');
  await page.getByRole('textbox',{name:'旧中文（可选）',exact:true}).fill('我的神鞭');await page.locator('#saveTerms').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('词库已保存'));
  check(savedTerms[0].legacyTarget==='我的神鞭','editing the old name is included in the saved glossary');
  const download=page.waitForEvent('download');await page.locator('#exportTerms').click();const file=await download;await file.saveAs(path.join(root,'artifacts/legacy-name-export.json'));const exported=JSON.parse(fs.readFileSync(path.join(root,'artifacts/legacy-name-export.json'),'utf8'));
  check(exported[0].legacyTarget==='我的神鞭','export preserves the optional legacy field');
  const oldFormat=[{source:'old-format',target:'当前名',english:'Custom',kind:'name',ambiguous:false}];
  async function importTerms(values,name){await page.locator('#termFile').setInputFiles({name,mimeType:'application/json',buffer:Buffer.from(JSON.stringify(values))});}
  await importTerms(oldFormat,'old-format.json');await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('已载入词库'));await page.locator('#termSearch').fill('');
  check(await page.getByRole('textbox',{name:'旧中文（可选）',exact:true}).inputValue()==='','old-format glossary imports with a blank old-name cell');
  await importTerms([{...oldFormat[0],legacyTarget:123}],'bad-number.json');await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('词库格式不正确'));check(await page.locator('#termRows tr').count()===1,'invalid new field does not replace loaded glossary');
  await importTerms([{...oldFormat[0],legacyTarget:'a'.repeat(151)}],'bad-length.json');await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('词库格式不正确'));check(true,'overlong old name is rejected on import');
  await importTerms([{...oldFormat[0],legacyTarget:'<img src=x onerror="window.injected=true">'}],'plain-text.json');await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('已载入词库'));check(await page.locator('#termRows img').count()===0&&!await page.evaluate(()=>window.injected),'old names render only in plain text inputs');
  await page.setViewportSize({width:600,height:760});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'extra glossary column stays inside its scroll container');
  await page.locator('[data-page="glossary"]').click();await page.screenshot({path:path.join(root,'artifacts/legacy-name-appearance-narrow.png'),fullPage:true});check(await page.locator('#chineseNameStyle').isVisible(),'display choice remains accessible in a narrow window');
  await page.locator('#chineseNameStyle').selectOption('current');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));check(savedConfig.chineseNameStyle==='current','current naming can be restored');
  check(errors.length===0,'new controls and imports have no browser errors');
  fs.writeFileSync(path.join(root,'artifacts/legacy-name-ui-tests.json'),JSON.stringify({passed,scope:'headless settings with a mock HTTP server; no real backend/game/cloud calls'},null,2));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
