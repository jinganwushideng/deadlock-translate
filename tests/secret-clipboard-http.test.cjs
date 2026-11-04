// Auth/origin boundaries on a real isolated backend. Valid paste requests are
// deliberately never sent, so this test cannot read the user's clipboard.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const root=path.join(__dirname,'..'),runtime=process.env.DLT_TEST_RUNTIME||path.join(root,'dist/DeadlockTranslate-'+JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version);
const task=path.join(root,'artifacts/password-input-20261104'),state=path.join(task,'http-'+Date.now());fs.mkdirSync(state,{recursive:true});
const key=crypto.randomBytes(24).toString('hex'),pair=path.join(state,'pairing.key');fs.writeFileSync(pair,key);
const checks=[],sleep=ms=>new Promise(r=>setTimeout(r,ms));let child,base,cookie;
const check=(ok,name)=>{assert(ok,name);checks.push(name);console.log('PASS '+name);};
const port=()=>new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
(async()=>{try{
 const p=await port(),tls=await port();base='http://localhost:'+p;child=spawn(path.join(runtime,'DeadlockTranslate.exe'),['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pair,'--port',String(p),'--https-port',String(tls)],{cwd:runtime,windowsHide:true,stdio:['ignore','pipe','pipe']});
 const log=fs.createWriteStream(path.join(state,'backend.log'));child.stdout.pipe(log);child.stderr.pipe(log);
 for(let n=0;n<100;n++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 const health=await(await fetch(base+'/health')).json();
 const request=(method,headers={},body)=>fetch(base+'/api/settings/paste-secret',{method,headers,body:body===undefined?undefined:JSON.stringify(body)});
 check((await request('POST',{'Content-Type':'application/json',Origin:base,'X-DLT-Request':'1'},{field:'llmKey'})).status===401,'unauthenticated paste cannot reach clipboard code');
 const login=await fetch(base+'/in-game/'+key);cookie=login.headers.getSetCookie().map(v=>v.split(';')[0]).join(';');
 const headers={Cookie:cookie,'Content-Type':'application/json',Origin:base,'X-DLT-Request':'1'};
 check((await request('GET',headers)).status===405,'GET/status polling cannot invoke clipboard reads');
 check((await request('POST',{...headers,Origin:'https://outside.invalid'},{field:'llmKey'})).status===403,'cross-origin requests cannot read the clipboard');
 const missing={...headers};delete missing['X-DLT-Request'];
 check((await request('POST',missing,{field:'azureKey'})).status===403,'missing explicit settings request marker is rejected');
 for(const field of ['model','PRIVATE_REQUEST_MARKER',null]){
  const response=await request('POST',headers,{field}),body=await response.json();
  check(response.status===400&&body.code==='clipboard-field'&&!JSON.stringify(body).includes('PRIVATE_REQUEST_MARKER'),'invalid field has a bounded content-free response');
 }
 const publicConfig=await(await fetch(base+'/api/config',{headers})).json();
 check(!publicConfig.hasLlmKey&&!publicConfig.hasAzureKey,'rejected paste requests do not create or erase credentials');
 fs.writeFileSync(path.join(task,'clipboard-http-tests.json'),JSON.stringify({passed:checks.length,checks,buildId:health.buildId,systemClipboardRead:false,validPasteSent:false,realGame:false,cloudCalls:0},null,2));
}finally{if(child){try{await fetch(base+'/api/stop',{method:'POST',headers:{Cookie:cookie||'',Origin:base,'X-DLT-Request':'1'}});}catch{}await sleep(350);if(child.exitCode===null)child.kill();}}})().catch(e=>{console.error(e);process.exitCode=1;});
