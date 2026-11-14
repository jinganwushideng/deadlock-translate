const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');
let passed=0,opens=0,saves=0,revision=1,settings={enabled:true,mode:'fast',llmBaseUrl:'https://api.deepseek.com/v1',model:'deepseek-flash',outgoingHotkey:'LAlt+Space'};
const check=(value,name)=>{assert(value,name);passed++;console.log('PASS '+name);};
const publicConfig=()=>({settings,hasLlmKey:true,hasAzureKey:false,revision:'r'+revision});
const server=http.createServer((req,res)=>{
 const url=req.url.split('?')[0];res.setHeader('Content-Type','application/json');
 if(url==='/api/config'&&req.method==='PUT'){
  let text='';req.on('data',data=>text+=data);req.on('end',()=>{settings=JSON.parse(text);saves++;revision++;res.end(JSON.stringify(publicConfig()));});
 }else if(url==='/api/config')res.end(JSON.stringify(publicConfig()));
 else if(url==='/api/terms')res.end('[]');
 else if(url==='/api/providers/deepseek/key'){opens++;res.end('{"opened":true}');}
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:false},terms:1,diagnostics:{phase:'waiting-for-game',certificate:{state:'ready'},gamePrivileges:{detected:false}}}));
 else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url.startsWith('/api/'))res.end('{}');
 else{
  const file=['/app.js','/style.css','/settings-navigation.js','/settings-background.js','/settings-background.css'].includes(url)?url.slice(1):'index.html';
  res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html; charset=utf-8');
  res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',file)));
 }
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const base='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1280,height:940}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  const load=async()=>{await page.goto(base+'/in-game/test');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="providers"]').click();};
  await load();
  check(await page.locator('#llmPreset').inputValue()==='deepseek','legacy official endpoint defaults to DeepSeek');
  check(await page.locator('#llmBaseUrl').isHidden()&&await page.locator('#deepseekGetKey').isVisible(),'DeepSeek shows key-platform button instead of API address');
  check(await page.locator('#model').evaluate(n=>n.tagName)==='SELECT','DeepSeek model name is a selection control');
  check(JSON.stringify(await page.locator('#model option').evaluateAll(nodes=>nodes.map(n=>n.value)))===JSON.stringify(['deepseek-flash','deepseek-v4-pro']),'common model allows exactly Flash and V4 Pro');
  check(await page.locator('#model').inputValue()==='deepseek-flash'&&saves===0,'Flash is selected without an unsolicited configuration write');
  const beforeUrl=page.url();await page.locator('#deepseekGetKey').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('DeepSeek 平台'));
  check(opens===1&&page.url()===beforeUrl,'key button calls backend once and keeps the game settings page open');
  await page.locator('#model').selectOption('deepseek-v4-pro');
  await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
  check(settings.model==='deepseek-v4-pro'&&!('llmKey' in settings),'Pro selection persists without clearing or exposing the saved key');
  await page.locator('#llmPreset').selectOption('custom');
  check(await page.locator('#llmBaseUrl').isVisible()&&await page.locator('#model').evaluate(n=>n.tagName)==='INPUT','custom service restores editable endpoint and model');
  await page.locator('#llmBaseUrl').fill('https://custom.example/v1');await page.locator('#model').fill('custom-model-id');
  await page.locator('#llmServiceCard h3').first().click();
  await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
  check(settings.llmPreset==='custom'&&settings.llmBaseUrl==='https://custom.example/v1'&&settings.model==='custom-model-id','custom endpoint and model save together');
  await load();
  check(await page.locator('#llmPreset').inputValue()==='custom'&&await page.locator('#model').inputValue()==='custom-model-id','custom choice survives reload');
  await page.locator('#llmPreset').selectOption('deepseek');
  check(await page.locator('#model').inputValue()==='deepseek-flash','switching to DeepSeek picks a supported default');
  await page.locator('#model').selectOption('deepseek-v4-pro');
  await page.waitForFunction(()=>document.querySelector('#autoSaveStatus').textContent==='所有更改已保存');
  await load();await page.locator('#llmPreset').selectOption('custom');
  check(await page.locator('#llmBaseUrl').inputValue()==='https://custom.example/v1'&&await page.locator('#model').inputValue()==='custom-model-id','custom endpoint and model restore after a DeepSeek save and reload');
  await page.locator('#llmPreset').selectOption('deepseek');await page.locator('#llmIndependentProfiles').check();await page.locator('#modelTab-incoming').click();
  check(await page.locator('#incomingModel').evaluate(n=>n.tagName)==='SELECT'&&await page.locator('#incomingModel option').count()===3,'independent model selection includes inheritance plus the two supported models');
  await page.locator('#incomingModel').selectOption('deepseek-flash');
  await page.locator('#modelTab-common').click();
  await page.screenshot({path:path.join(root,'artifacts/deepseek-preset-desktop.png'),fullPage:true});
  await page.setViewportSize({width:720,height:900});await page.screenshot({path:path.join(root,'artifacts/deepseek-preset-small.png'),fullPage:true});
  check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'preset controls fit a smaller settings window');
  check(errors.length===0,'preset switches cause no browser errors');
  fs.writeFileSync(path.join(root,'artifacts/deepseek-preset-ui-results.json'),JSON.stringify({passed,saves,opens,realApi:false,realBrowserNavigation:false},null,2));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
