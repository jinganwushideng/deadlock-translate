// Product page with a fake clipboard endpoint. Never touches the system clipboard.
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
const {chromium}=require('playwright');const root=path.join(__dirname,'..');
let settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',fontSize:17,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'eastasia',transport:'fragment'};
const clips=[],writes=[],secrets={llm:'',azure:''};let fail=false,delay=0,nativeRequests=0;
const fake={llmKey:'PRIVATE_LLM_CLIPBOARD_MARKER',azureKey:'PRIVATE_AZURE_CLIPBOARD_MARKER'};
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://fixture.invalid');let text='';for await(const b of req)text+=b;const input=text?JSON.parse(text):null;
 if(url.pathname.startsWith('/api/')){
  res.setHeader('Content-Type','application/json');
  if(url.pathname==='/api/settings/paste-secret'){
   clips.push({method:req.method,body:input});if(delay)await new Promise(r=>setTimeout(r,delay));
   if(fail){res.statusCode=409;return res.end(JSON.stringify({error:'剪贴板没有文字；请先复制 API 密钥。',code:'clipboard-empty'}));}
   return res.end(JSON.stringify({value:fake[input.field]}));
  }
  if(url.pathname==='/api/config'){
   if(req.method==='PUT'){writes.push(input);if('llmKey'in input)secrets.llm=input.llmKey;if('azureKey'in input)secrets.azure=input.azureKey;settings={...input};delete settings.llmKey;delete settings.azureKey;}
   return res.end(JSON.stringify({settings,hasLlmKey:!!secrets.llm,hasAzureKey:!!secrets.azure}));
  }
  if(url.pathname==='/api/native-editor'){nativeRequests++;return res.end('{}');}
  if(url.pathname==='/api/terms')return res.end('[]');
  if(url.pathname==='/api/outgoing')return res.end('{"items":[]}');
  if(url.pathname==='/api/status')return res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:0,diagnostics:{phase:'connected',certificate:{state:'ready'},hotkey:{available:true},sending:{state:'idle'}}}));
  return res.end('{}');
 }
 const asset=url.pathname.slice(1),name=['style.css','app.js','settings-background.css','settings-background.js','settings-background-v3-flat.png','settings-navigation.js'].includes(asset)?asset:'index.html';
 res.setHeader('Content-Type',name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',name)));
});
const checks=[];function check(ok,name){assert(ok,name);checks.push(name);console.log('PASS '+name);}
async function until(ready){const deadline=Date.now()+6000;while(!ready()&&Date.now()<deadline)await new Promise(r=>setTimeout(r,20));assert(ready(),'expected clipboard save did not reach the fixture');}
(async()=>{let browser;try{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
 browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
 await page.locator('nav [data-page="providers"]').click();await page.waitForTimeout(1100);
 check(clips.length===0,'opening settings and status polling never read the clipboard');
 check(await page.locator('#llmKey').getAttribute('type')==='password'&&await page.locator('#azureKey').getAttribute('type')==='password','both credentials retain password masking');
 await page.locator('#pasteLlmKey').click();await until(()=>secrets.llm===fake.llmKey);await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存'&&document.querySelector('#llmKey').value==='');
 check(clips.length===1&&clips[0].method==='POST'&&JSON.stringify(clips[0].body)==='{"field":"llmKey"}','one explicit click requests one field without sending clipboard data in the request');
 check(secrets.llm===fake.llmKey&&secrets.azure==='','only the chosen provider secret is saved');
 check(await page.locator('#llmKey').inputValue()===''&&(await page.locator('#llmKey').getAttribute('placeholder')).includes('已保存'),'successful save clears the transient field and shows only a saved marker');
 check(!(await page.title()).includes(fake.llmKey)&&!(await page.locator('body').innerText()).includes(fake.llmKey)&&nativeRequests===0,'credential bypasses native game drafts and all visible title/status text');
 const llm=secrets.llm;await page.locator('#pasteAzureKey').click();await until(()=>secrets.azure===fake.azureKey);await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存'&&document.querySelector('#azureKey').value==='');
 check(secrets.azure===fake.azureKey&&secrets.llm===llm,'Azure paste leaves the other saved credential unchanged');
 const before=writes.length;fail=true;await page.locator('#pasteLlmKey').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('剪贴板没有文字'));await page.waitForTimeout(600);
 check(secrets.llm===llm&&writes.length===before,'empty clipboard does not clear or rewrite an existing key');
 fail=false;delay=450;const n=clips.length;await page.locator('#pasteLlmKey').click();await page.waitForFunction(count=>document.querySelector('#pasteLlmKey').disabled,n);
 await page.locator('#llmKey').fill('NEW_MANUAL_DRAFT');await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('未覆盖新输入'));
 check(await page.locator('#llmKey').inputValue()==='NEW_MANUAL_DRAFT','delayed clipboard result cannot overwrite newer manual input');
 await page.locator('#pageTitle').click();await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');delay=0;
 const reads=clips.length;await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('nav [data-page="providers"]').click();
 check(clips.length===reads&&await page.locator('#llmKey').inputValue()==='','reload neither reads the clipboard nor returns the saved secret to the page');
 const shots=path.join(root,'artifacts/password-input-20261104');fs.mkdirSync(shots,{recursive:true});
 await page.setViewportSize({width:1920,height:1200});await page.locator('article').filter({has:page.locator('#llmKey')}).screenshot({path:path.join(shots,'secret-controls-wide.png')});
 await page.setViewportSize({width:510,height:900});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'key and paste controls fit narrow settings windows');
 await page.locator('.secret-key-row').filter({has:page.locator('#llmKey')}).screenshot({path:path.join(shots,'secret-controls-narrow.png')});
 check(errors.length===0,'secret paste leaves the product page free of browser script errors');
 const output=path.join(root,'artifacts/password-input-20261104/clipboard-ui-tests.json');fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify({passed:checks.length,checks,systemClipboardUsed:false,realCredentialsUsed:false,realGame:false,cloudCalls:0},null,2));
}finally{if(browser)await browser.close();server.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
