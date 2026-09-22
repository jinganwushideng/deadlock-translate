// Render actual packaged settings pages against an isolated backend; never open the game.
const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const version=JSON.parse(fs.readFileSync(path.resolve(__dirname,'../version.json'),'utf8')).version;const root=path.resolve(__dirname,'..'),base='http://localhost:17868',key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim();
 const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const page=await browser.newPage({viewport:{width:1440,height:1100},reducedMotion:"reduce"});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(session.url);await page.waitForFunction(()=>document.querySelector('#termCount').textContent!=='0');
 await page.route('**/api/outgoing',route=>route.fulfill({json:{hotkeyAvailable:true,items:[
  {id:'demo1',state:'submitted',channel:'party',displayMode:'bilingual',original:'等我大招好了再一起推进。',english:'Wait for my ult, then push together.',sendText:'等我大招好了再一起推进。 | Wait for my ult, then push together.',error:null},
  {id:'demo2',state:'failed',channel:'team',displayMode:'translation',original:'别追，先回去守家。',english:'Don’t chase. Fall back and defend.',sendText:'Don’t chase. Fall back and defend.',error:'译文已过期，未自动发送'}]}}));
 let layoutChecks=0;
 for(const width of [1440,1024,700,390]){
  await page.setViewportSize({width,height:width===1440?1100:900});
  for(const section of ['overview','providers','outgoing','glossary','appearance']){
   await page.locator('nav button[data-page="'+section+'"]').click();await page.evaluate(()=>window.scrollTo(0,0));await page.mouse.move(0,0);
   assert(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1),'page overflow: '+section+' at '+width);
   assert(await page.locator('#save').isVisible(),'save button missing');layoutChecks++;
   if(width===1440||(width===390&&['overview','outgoing','appearance'].includes(section))){
    if(section==='outgoing')await page.waitForFunction(()=>document.querySelector('#outgoingItems').textContent.includes('队伍（组队）'));
    await page.screenshot({path:path.join(root,'artifacts','ui-'+version.replace(/\./g,'')+'-'+section+'-'+width+'.png'),fullPage:true});
   }
  }
 }
 await page.locator('#fontSize').selectOption('24');
 assert.equal(await page.locator('[data-translation-preview]').evaluate(el=>getComputedStyle(el).fontSize),'24px');
 const colour=await page.locator('.translation').first().evaluate(el=>({text:getComputedStyle(el).color,bg:getComputedStyle(el).backgroundColor}));
 assert.deepEqual(colour,{text:'rgb(40, 36, 30)',bg:'rgb(233, 222, 200)'});
 assert.deepEqual(errors,[]);await browser.close();
 const result={layoutChecks,previewChecks:2,widths:[1440,1024,700,390],pageErrors:errors,at:new Date().toISOString(),scope:'actual settings in headless Edge; Panorama CSS compiled separately; game layout and FPS not measured'};
 fs.writeFileSync(path.join(root,'artifacts/ui-visual-tests.json'),JSON.stringify(result,null,2));console.log(result);
})().catch(e=>{console.error(e);process.exit(1)});
