const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),out=path.join(root,'artifacts/send-ui-recovery-20261026');fs.mkdirSync(out,{recursive:true});
let passed=0,saves=0,termsSaved=0;const checks=[];function check(value,name){assert(value,name);passed++;checks.push(name);}
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',fontSize:17,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,model:'fixture',llmBaseUrl:'https://fixture.invalid/v1',outgoingDisplayMode:'bilingual',outgoingSeparateBilingual:true,outgoingSplitLong:true,incomingDisplayMode:'both',transport:'fragment'};
const pages=['overview','incoming','outgoing','shortcuts','providers','glossary','appearance','performance','diagnostics'];
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];
 page.on('pageerror',error=>errors.push(error.message));
 await page.route('http://dlt.test/**',async route=>{
  const request=route.request(),url=new URL(request.url()),p=url.pathname;
  if(p.startsWith('/api/')){
   if(p==='/api/config'){if(request.method()==='PUT'){Object.assign(settings,request.postDataJSON());saves++;}return route.fulfill({json:{settings,hasLlmKey:false,hasAzureKey:false}});}
   if(p==='/api/terms'){if(request.method()==='PUT')termsSaved++;return route.fulfill({json:[{source:'Apollo',target:'阿波罗',english:'Apollo',kind:'name',ambiguous:false}]});}
   const diagnostics={phase:'connected',version:'0.6.25',clientVersion:'0.6.25',buildId:'fixture',clientBuildId:'fixture',certificate:{message:'证书正常',state:'ready'},hotkey:{available:true,triggered:0,acknowledged:0},sending:{state:'idle'}};
   if(p==='/api/status')return route.fulfill({json:{engine:{messages:12,pending:0,connected:true},terms:1312,diagnostics}});
   if(p==='/api/diagnostics')return route.fulfill({json:diagnostics});
   if(p==='/api/outgoing')return route.fulfill({json:{items:[],hotkeyAvailable:true,hotkey:diagnostics.hotkey}});
   return route.fulfill({json:{}});
  }
  if(p==='/community-qq.png')return route.fulfill({path:path.join(root,'src/Bridge/wwwroot/community-qq.png')});
  const file=p==='/app.js'?'app.js':p==='/style.css'?'style.css':'index.html';
  return route.fulfill({body:fs.readFileSync(path.join(root,'src/Bridge/wwwroot',file)),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html; charset=utf-8'});
 });
 try{
  await page.goto('http://dlt.test/');await page.waitForFunction(()=>document.querySelector('#model').value==='fixture');
  const css=fs.readFileSync(path.join(root,'src/Bridge/wwwroot/style.css'));
  check(css.equals(fs.readFileSync(path.join(root,'release/0.6.25/DeadlockTranslate-0.6.25/wwwroot/style.css'))),'style is byte-for-byte the formal 0.6.25 release');
  check(!css.toString('utf8').includes('brush-violet')&&!css.toString('utf8').includes('feTurbulence'),'purple experimental decoration and live noise filters are absent');
  for(const viewport of [{width:2560,height:1600},{width:1920,height:1080},{width:1280,height:800},{width:960,height:700},{width:600,height:760}]){
   await page.setViewportSize(viewport);
   for(const id of pages){
    await page.locator('aside nav [data-page="'+id+'"]').click();await page.waitForFunction(id=>!document.getElementById(id).hidden,id);
    check(await page.evaluate(id=>document.querySelector('aside nav button.active')?.dataset.page===id&&document.querySelectorAll('.page:not([hidden])').length===1,id),'single-click page navigation '+id+' '+viewport.width);
    check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'no page-wide horizontal overflow '+id+' '+viewport.width);
   }
  }
  await page.setViewportSize({width:1440,height:960});await page.locator('[data-page="overview"]').click();
  check(await page.evaluate(()=>getComputedStyle(document.querySelector('aside nav button')).textShadow==='none'),'formal navigation has no purple glow');
  await page.screenshot({path:path.join(out,'settings-overview.png')});
  await page.locator('[data-go-page="outgoing"]').click();await page.waitForFunction(()=>!document.querySelector('#outgoing').hidden);
  check(true,'existing overview shortcut opens sending settings');
  await page.locator('#outgoingDisplayMode').selectOption('translation');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  check(settings.outgoingDisplayMode==='translation'&&saves===1,'translation-only mode saves through the unchanged footer');
  await page.locator('[data-page="providers"]').click();await page.locator('#model').fill('model-v2');await page.locator('#saveSidebar').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  check(settings.model==='model-v2'&&saves===2,'sidebar save uses the same persistence operation');
  await page.screenshot({path:path.join(out,'settings-providers.png')});
  for(const id of pages)await page.locator('[data-page="'+id+'"]').click();check(saves===2&&termsSaved===0,'navigation does not write settings or terms');
  await page.locator('[data-page="glossary"]').click();await page.locator('#termSearch').fill('Apollo');check(await page.locator('#termRows tr').count()===1,'browser glossary editing remains available');
  await page.setViewportSize({width:600,height:760});await page.screenshot({path:path.join(out,'settings-narrow.png')});
  await page.goto('http://dlt.test/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready'));
  check(await page.locator('#nativeInputHelp').isVisible()&&await page.locator('#nativeInputEnabled').isVisible(),'original native input compatibility controls remain visible');
  check(errors.length===0,'restored UI has no browser errors');
  fs.writeFileSync(path.join(out,'workbench-tests.json'),JSON.stringify({passed,checks,saves,termsSaved,theme:'formal release 0.6.25',realGame:false,realApi:false},null,2));console.log('PASS '+passed+' formal UI checks');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
