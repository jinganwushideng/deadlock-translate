const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..');let passed=0,saved,writes=0,stops=0,revision=0;const previews=[];
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'quality',fastProvider:'bing',fontSize:17,scanIntervalMs:200,fastConcurrency:4,llmConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,transport:'fragment',llmThinkingEnabled:true};
const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
async function until(fn){for(let i=0;i<100;i++){if(fn())return;await new Promise(r=>setTimeout(r,30));}throw Error('fixture condition timed out');}
const server=http.createServer(async(req,res)=>{res.setHeader('Content-Type','application/json');const url=req.url.split('?')[0];
 if(url==='/api/config'&&req.method==='PUT'){let body='';for await(const c of req)body+=c;saved=JSON.parse(body);writes++;Object.assign(settings,saved);res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));}
 else if(url==='/api/display-preview'&&req.method==='POST'){let body='';for await(const c of req)body+=c;const options=JSON.parse(body);previews.push(options);res.end(JSON.stringify({epoch:'fixture',revision:++revision,active:true,remainingMs:12000,options}));}
 else if(url==='/api/display-preview'&&req.method==='DELETE'){stops++;res.end(JSON.stringify({epoch:'fixture',revision:++revision,active:false,remainingMs:0}));}
 else if(url==='/api/config')res.end(JSON.stringify({settings,hasLlmKey:true,hasAzureKey:false}));
 else if(url==='/api/terms')res.end('[]');else if(url==='/api/outgoing')res.end('{"items":[]}');
 else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:10,diagnostics:{phase:'connected',certificate:{state:'ready'}}}));
 else if(url.startsWith('/api/'))res.end('{}');
 else if(url==='/app.js'||url==='/style.css'){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1180,height:950}}),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/in-game/fixture');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="appearance"]').click();
 check(await page.locator('#incomingDisplayMode option').count()===3,'native independent and simultaneous modes are available');
 check(await page.locator('#incomingFeedLines').getAttribute('max')==='10','custom line cap is ten');
 await page.locator('#incomingDisplayMode').selectOption('both');
 await page.locator('#incomingFeedWidth').fill('480');await page.locator('#incomingOpacity').fill('55');await page.locator('#incomingShowAccountName').uncheck();await page.locator('#incomingHeroNamePosition').selectOption('after');await page.locator('#incomingFeedPosition').selectOption('bottom-right');await page.locator('#incomingFeedOffsetX').fill('60');await page.locator('#incomingFeedOffsetY').fill('30');await page.locator('#incomingFeedLines').fill('10');
 await until(()=>previews.at(-1)?.lines===10&&previews.at(-1)?.width===480);
 check(writes===0,'adjusting controls does not save configuration');
 check(previews.at(-1).mode==='both'&&previews.at(-1).opacity===55&&!previews.at(-1).showAccountName,'draft preview receives mode opacity and identity switches');
 check(previews.at(-1).position==='bottom-right'&&previews.at(-1).offsetX===60&&previews.at(-1).offsetY===30,'draft preview receives exact placement choices');
 check(await page.locator('#displayPreviewStage').count()===0,'no inset imitation stage substitutes for actual game position');
 check(previews.at(-1).showAccountName===false,'account-name choice is delivered to actual game preview');
 check((await page.locator('#incomingOpacityValue').textContent()).includes('55%'),'opacity slider has a readable numeric value');
 await page.locator('#incomingShowAccountName').check();await page.locator('#incomingHeroNamePosition').selectOption('after');
 await until(()=>previews.at(-1)?.showAccountName===true&&previews.at(-1)?.heroNamePosition==='after');check(true,'hero order is delivered to game preview');
 await page.locator('#previewDisplay').click();await until(()=>previews.at(-1)?.showAccountName===true);
 check((await page.locator('#displayPreviewStatus').textContent()).includes('没有保存设置'),'native preview request reports its temporary nature');
 await page.locator('#stopDisplayPreview').click();check(stops>0&&writes===0,'stop closes only the preview without saving');
 await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#saveState').textContent.includes('已保存'));
 check(saved.incomingDisplayMode==='both'&&saved.incomingFeedLines===10&&saved.incomingFeedWidth===480&&saved.incomingOpacity===55,'save persists typed combined layout settings');
 check(saved.incomingShowAccountName===true&&saved.incomingHeroNamePosition==='after'&&saved.llmThinkingEnabled===true,'save retains identity order and model thinking');
 await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="appearance"]').click();check(await page.locator('#incomingFeedLines').inputValue()==='10'&&await page.locator('#incomingFeedPosition').inputValue()==='bottom-right','layout and ten-line setting survive reload');
 await page.locator('#appearance').screenshot({path:path.join(root,'artifacts/display-customization-wide.png')});await page.setViewportSize({width:510,height:950});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'appearance controls fit narrow settings windows');await page.locator('#appearance').screenshot({path:path.join(root,'artifacts/display-customization-narrow.png')});
 await page.locator('#previewDisplay').click();const before=stops;await page.locator('[data-page="overview"]').click();await until(()=>stops>before);check(true,'leaving appearance stops active preview');
 check(errors.length===0,'appearance customization has no browser script errors');fs.writeFileSync(path.join(root,'artifacts/display-customization-ui-tests.json'),JSON.stringify({passed,realBackend:false,realGame:false}));console.log('TOTAL '+passed+' passed');
}finally{await browser.close();await new Promise(r=>server.close(r));}})().catch(e=>{console.error(e);process.exitCode=1;});
