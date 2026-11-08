// Exercise the shipped settings DOM and JavaScript with a local mock backend.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('playwright');
const root=path.join(__dirname,'..','src','Bridge','wwwroot');
const origin='http://127.0.0.1:19073';

(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1280,height:900}});
  const calls=[];
  await page.route('**/*',async route=>{
   const url=new URL(route.request().url());
   if(url.origin!==origin){await route.abort();return;}
   let value;
   if(url.pathname.startsWith('/api/')){
    const action=url.pathname.slice(5);
    const ids=route.request().postDataJSON()?.accountIds??[];
    if(action==='config')value={settings:{},hasLlmKey:false,hasAzureKey:false};
    else if(action==='terms')value=[];
    else if(action==='status')value={engine:{messages:0,pending:0,connected:true},terms:0};
    else if(action==='outgoing')value={items:[]};
    else if(action==='maintenance/steam/accounts')value={users:[{id:'101',name:'Primary',active:true},{id:'102',name:'Alternate'},{id:'103',name:'Untouched'}]};
    else if(action==='maintenance/steam/preview'){
     calls.push(['preview',ids]);value={blocked:false,accounts:ids.map(id=>({id,alreadyConfigured:false,skipped:id==='102',reason:'无本地游戏记录'}))};
    }else if(action==='maintenance/steam/open'){calls.push(['open',ids]);value={opened:true};}
    else value={};
    await route.fulfill({json:value});return;
   }
   const file=url.pathname.startsWith('/in-game/')?'index.html':url.pathname.slice(1);
   const mime={'.html':'text/html; charset=utf-8','.css':'text/css','.js':'application/javascript','.png':'image/png'}[path.extname(file)];
   const full=path.join(root,file);
   if(mime&&fs.existsSync(full))await route.fulfill({path:full,contentType:mime});
   else await route.fulfill({status:404});
  });
  await page.goto(origin+'/in-game/fixture');
  await page.locator('button[data-page="diagnostics"]').click();
  await page.locator('#steamAccountLoad').click();
  const second=page.locator('#steamAccountList input[value="102"]');
  await second.check();
  assert(await page.locator('#steamAccountList input[value="101"]').isChecked());
  await page.locator('#steamAccountReview').click();
  await page.locator('#steamAccountOpen').waitFor({state:'visible'});
  assert((await page.locator('#steamAccountPreview').textContent()).includes('跳过'));
  await page.locator('#steamAccountOpen').click();
  await page.waitForFunction(()=>document.querySelector('#steamAccountState').textContent.includes('已打开'));
  assert.deepEqual(calls,[['preview',['101','102']],['open',['101','102']]]);
  await second.uncheck();
  assert(await page.locator('#steamAccountOpen').isHidden());
  assert.equal(await page.locator('#steamAccountPreview li').count(),0);
  await page.locator('#steamAccountList input[value="101"]').uncheck();
  await second.check();
  await page.locator('#steamAccountReview').click();
  await page.waitForFunction(()=>document.querySelector('#steamAccountState').textContent.includes('全部跳过'));
  assert(await page.locator('#steamAccountOpen').isHidden());
  console.log('PASS in-game Steam selection forwards exactly two checked accounts and invalidates stale previews');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
