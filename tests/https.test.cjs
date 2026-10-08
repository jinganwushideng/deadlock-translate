const {chromium}=require('playwright');
const fs=require('node:fs'),path=require('node:path'),https=require('node:https'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim();
 const state=process.env.DLT_TLS_TEST_STATE;assert(state,'isolated test state required');
 const der=fs.readFileSync(path.join(state,'localhost-certificate.cer'));
 const pem='-----BEGIN CERTIFICATE-----\n'+der.toString('base64').match(/.{1,64}/g).join('\n')+'\n-----END CERTIFICATE-----\n';
 const base='https://localhost:17869';
 const request=(route,options={})=>new Promise((resolve,reject)=>{const r=https.request(base+route,{ca:pem,...options},res=>{let body='';res.on('data',b=>body+=b);res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body}));});r.on('error',reject);if(options.body)r.write(options.body);r.end();});
 const health=await request('/health');assert.equal(health.status,200);assert.equal(JSON.parse(health.body).version,JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version);
 assert.equal((await request('/api/config')).status,401);
 assert.equal((await request('/in-game/wrong')).status,404);
 const login=await request('/in-game/'+key);assert.equal(login.status,200);assert(!login.headers.location);
 const cookie=login.headers['set-cookie'][0];assert(cookie.includes('dlt-session-tls='));assert(/secure/i.test(cookie));assert(/httponly/i.test(cookie));
 const config=await request('/api/config',{headers:{Cookie:cookie.split(';')[0]}});assert.equal(config.status,200);
 const body=JSON.stringify(JSON.parse(config.body).settings);
 assert.equal((await request('/api/config',{method:'PUT',headers:{Cookie:cookie.split(';')[0],Origin:'http://localhost:17868','X-DLT-Request':'1','Content-Type':'application/json'},body})).status,403);
 assert.equal((await request('/api/config',{method:'PUT',headers:{Cookie:cookie.split(';')[0],Origin:base,'X-DLT-Request':'1','Content-Type':'application/json'},body})).status,200);
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
  const strict=await browser.newContext();const strictPage=await strict.newPage();
  await assert.rejects(()=>strictPage.goto(base+'/health'),/ERR_CERT_AUTHORITY_INVALID/);await strict.close();
  // A disposable test context only; never disables certificate validation in the shipped app.
  const context=await browser.newContext({ignoreHTTPSErrors:true});const page=await context.newPage();
  await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  await page.locator('#mode').selectOption('fast');await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));
  await page.reload();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));assert.equal(await page.locator('#mode').inputValue(),'fast');
  const bridge=await context.newPage();await bridge.goto(base+'/bridge/'+key);assert.equal(await bridge.title(),'DLT:ready');
  await bridge.evaluate(()=>{location.hash=encodeURIComponent(JSON.stringify({session:'isolated-https-fixture',seq:9,messages:[],poll:[]}));});
  await bridge.waitForFunction(()=>document.title.startsWith('DLT:{'));assert.equal(JSON.parse((await bridge.title()).slice(4)).seq,9);
 } finally {await browser.close();}
 const report={passed:13,scope:'TLS certificate validation via explicit test CA; browser trust rejection, secure cookie, origin checks, settings save and bridge; no OS trust installation, live API calls or game input'};
 fs.writeFileSync(path.join(root,'artifacts/https-tests.json'),JSON.stringify(report,null,2));console.log(report);
})().catch(e=>{console.error(e);process.exit(1)});
