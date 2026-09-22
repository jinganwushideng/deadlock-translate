if (process.env.DLT_ALLOW_REAL_API !== '1') { throw new Error('Real-provider test disabled. Use an isolated backend and explicitly set DLT_ALLOW_REAL_API=1.'); }
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict');
(async()=>{
 const base='http://localhost:17865',key=fs.readFileSync(path.join(__dirname,'../artifacts/pairing.key'),'utf8').trim();
 const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();
 const auth=await fetch(session.url,{redirect:'manual'});const cookie=auth.headers.get('set-cookie').split(';')[0];
 const headers={Cookie:cookie,Origin:base,'Content-Type':'application/json','X-DLT-Request':'1'};
 const config=await(await fetch(base+'/api/config',{headers})).json();
 assert.equal(config.hasLlmKey,false,'must use isolated integration state without real keys');
 const seen=[];const mock=http.createServer(async(req,res)=>{let body='';for await(const chunk of req)body+=chunk;const data=JSON.parse(body);seen.push(data);res.setHeader('Content-Type','application/json');res.end(JSON.stringify({choices:[{message:{content:'别打，等我大招。'}}]}));});
 await new Promise(resolve=>mock.listen(17866,'127.0.0.1',resolve));
 try{
  let r=await fetch(base+'/api/config',{method:'PUT',headers,body:JSON.stringify({...config.settings,llmBaseUrl:'http://127.0.0.1:17866/v1',llmKey:'fixture-key-only',model:'fixture'})});assert.equal(r.status,200);
  r=await fetch(base+'/api/test',{method:'POST',headers,body:JSON.stringify({provider:'quality',text:"don't fight, wait for my ult"})});const llm=await r.json();assert.equal(r.status,200);assert.equal(llm.translation,'别打，等我大招。');assert.equal(seen[0].stream,false);assert.equal(seen[0].messages[0].role,'system');const payload=JSON.parse(seen[0].messages[1].content);assert.equal(payload.current,"don't fight, wait for my ult");assert(payload.glossary.some(t=>t.source==='ult'));
  r=await fetch(base+'/api/test',{method:'POST',headers,body:JSON.stringify({provider:'fast',text:'Please meet me by the bridge in 30 seconds.'})});const fast=await r.json();
  const report={at:new Date().toISOString(),mockLlm:{passed:6,note:'real HTTP serialization against local fixture, no live LLM account'},bing:{status:r.status,...fast}};
  fs.writeFileSync(path.join(__dirname,'../artifacts/provider-tests.json'),JSON.stringify(report,null,2));console.log(report);
 }finally{await fetch(base+'/api/config',{method:'PUT',headers,body:JSON.stringify({...config.settings,llmKey:''})});mock.close();}
})().catch(e=>{console.error(e);process.exit(1);});
