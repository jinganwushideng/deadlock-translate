const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
const fixture=path.join(__dirname,'native-editor-ui.test.cjs'),loaded=new Module(fixture,module);
loaded.filename=fixture;loaded.paths=Module._nodeModulePaths(__dirname);
loaded._compile(fs.readFileSync(fixture,'utf8').split('(async()=>{')[0]+String.fromCharCode(10)+'module.exports={server};',fixture);
const server=loaded.exports.server;
(async()=>{
 let browser;
 try{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  browser=await chromium.launch({channel:'msedge',headless:true});
  const page=await browser.newPage({viewport:{width:1280,height:850}});
  const errors=[],calls=[];page.on('pageerror',error=>errors.push(error.message));
  const status={currentVersion:'0.7.5',available:false,version:null,size:0,checkError:'GitHub unavailable fixture',download:'idle',downloaded:0,automatic:true};
  await page.route('**/api/updates',route=>route.fulfill({json:status}));
  let finishProbe;let probe=new Promise(resolve=>finishProbe=resolve);
  let result={source:'github',message:'GitHub 速度正常，已选择 GitHub 发布页。'},responseCode=200;
  await page.route('**/api/updates/open/**',async route=>{
   const destination=new URL(route.request().url()).pathname;
   calls.push(destination);
   if(destination.endsWith('/auto')){await probe;await route.fulfill({status:responseCode,json:result});}
   else await route.fulfill({json:{opened:true}});
  });
  await page.goto('http://127.0.0.1:'+server.address().port+'/');
  await page.waitForFunction(()=>document.querySelector('#connection').textContent==='后台已连接');
  await page.locator('nav [data-page="diagnostics"]').click();
  const download=page.locator('#releaseDownload'),hint=page.locator('#releaseLinkStatus');
  assert(await download.isVisible(),'primary route selection stays available even when GitHub metadata cannot be read');
  await download.click();
  assert(await download.isDisabled());
  assert.match(await hint.textContent(),/正在检测 GitHub/);
  finishProbe();
  await page.waitForFunction(()=>document.querySelector('#releaseLinkStatus').textContent.includes('速度正常'));
  assert(await download.isEnabled());
  probe=Promise.resolve();
  result={source:'quark',message:'GitHub 速度较慢，已选择夸克网盘。'};
  await download.click();
  await page.waitForFunction(()=>document.querySelector('#releaseLinkStatus').textContent.includes('速度较慢'));
  assert.deepEqual(calls,['/api/updates/open/auto','/api/updates/open/auto']);
  await page.locator('#releaseOpenBaidu').click();
  assert.equal(calls.at(-1),'/api/updates/open/baidu');
  responseCode=502;result={error:'Browser fixture failure'};
  await download.click();
  await page.waitForFunction(()=>document.querySelector('#releaseLinkStatus').textContent==='Browser fixture failure');
  assert(await download.isEnabled());
  assert.deepEqual(errors,[]);
  console.log('PASS primary download tests the route, shows progress and destination, remains usable without metadata, retains manual channels, and recovers from opening errors.');
 }finally{if(browser)await browser.close();server.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
