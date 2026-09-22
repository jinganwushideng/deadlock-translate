if (process.env.DLT_ALLOW_REAL_API !== '1') { throw new Error('Real-provider test disabled. Use an isolated backend and explicitly set DLT_ALLOW_REAL_API=1.'); }
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
(async()=>{
 const dir=path.join(__dirname,'../dist/DeadlockTranslate-0.1.0'),base='http://localhost:17865',key=fs.readFileSync(path.join(dir,'pairing.key'),'utf8').trim();
 const s=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();
 const response=await fetch(s.url,{redirect:'manual'}),cookie=response.headers.get('set-cookie').split(';')[0];
 const headers={Cookie:cookie,Origin:base,'X-DLT-Request':'1','Content-Type':'application/json'};
 const config=await(await fetch(base+'/api/config',{headers})).json();
 assert.equal(config.hasLlmKey,true);assert.equal(config.settings.model,'DeepSeek-V4-Flash-0731-Event');assert.equal(config.settings.llmBaseUrl,'https://api.scnet.cn/api/llm/v1');
 const direct=await(await fetch(base+'/api/test',{method:'POST',headers,body:JSON.stringify({provider:'quality',text:"don't finish boss until they die"})})).json();assert(direct.translation.includes('最后一击'));
 const liveResponse=await fetch(base+'/api/test',{method:'POST',headers,body:JSON.stringify({provider:'quality',text:'Seven has no ult'})});const live=await liveResponse.json();assert.equal(liveResponse.status,200);assert(live.translation.includes('柒（Seven）'));
 let game=await(await fetch(base+'/game/'+key,{method:'POST',headers,body:JSON.stringify({session:'release-smoke',seq:1,messages:[{id:'1',text:'назад'}],poll:['1']})})).json();
 for(let i=0;i<30&&!game.results.some(r=>r.done);i++){await new Promise(resolve=>setTimeout(resolve,30));game=await(await fetch(base+'/game/'+key,{method:'POST',headers,body:JSON.stringify({session:'release-smoke',seq:i+2,messages:[],poll:['1']})})).json();}
 assert(game.results.some(r=>r.text==='撤退'&&r.done));
 const browser=await chromium.launch({channel:'msedge',headless:true});const page=await browser.newPage({viewport:{width:1400,height:960}});await page.goto(s.url);await page.waitForFunction(()=>document.querySelector('#model').value==='DeepSeek-V4-Flash-0731-Event');await page.locator('nav button[data-page="providers"]').click();await page.screenshot({path:path.join(__dirname,'../artifacts/settings-configured.png'),fullPage:true});await browser.close();
 const result={passed:7,packagedExe:true,configuredModel:config.settings.model,liveTranslation:live.translation,liveElapsedMs:live.elapsedMs,exactPhrase:direct.translation,gameBridge:'HTTP synthetic message only; no Deadlock window tested',at:new Date().toISOString()};fs.writeFileSync(path.join(__dirname,'../artifacts/final-smoke.json'),JSON.stringify(result,null,2));console.log(result);
})().catch(e=>{console.error(e);process.exit(1)});
