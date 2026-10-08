const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
(async()=>{
 const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version;
 const state=JSON.parse(fs.readFileSync(path.join(root,'artifacts/optimization-tests.json'),'utf8')).expiredState;
 const key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim(),base='http://localhost:17918';
 const child=spawn(path.join(root,'dist/DeadlockTranslate-'+version+'/DeadlockTranslate.exe'),['--port','17918','--https-port','17919','--data-dir',state,'--pairing-file',path.join(root,'artifacts/pairing.key'),'--no-hotkey'],{windowsHide:true,stdio:'ignore'});
 let cookie;
 try{
  let health;for(let i=0;i<60;i++){try{health=await(await fetch(base+'/health',{signal:AbortSignal.timeout(500)})).json();break;}catch{await new Promise(r=>setTimeout(r,200));}}
  assert(health,'HTTP diagnostics should start despite expired certificate');assert.equal(health.httpsReady,false);
  const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();const login=await fetch(session.url,{redirect:'manual'});cookie=login.headers.get('set-cookie').split(';')[0];
  const headers={Cookie:cookie,Origin:base,'X-DLT-Request':'1'};
  const d=await(await fetch(base+'/api/diagnostics',{headers})).json();assert.equal(d.certificate.state,'expired');assert.equal(d.phase,'https-unavailable');
  const page=await fetch(base+'/',{headers});assert.equal(page.status,200);assert((await page.text()).includes('checkConnection'));
  assert.equal((await fetch(base+'/api/config',{headers})).status,200);
  const status=await(await fetch(base+'/api/status',{headers})).json();assert.equal(status.gameEvidence,null,'old on-disk metrics must not be presented as current activity');
  fs.writeFileSync(path.join(root,'artifacts/expired-certificate-tests.json'),JSON.stringify({passed:6,scope:'packaged backend with expired isolated certificate; HTTP settings/diagnostics remain accessible; no trust store changes'},null,2));console.log('PASS 6 expired-certificate recovery checks');
 }finally{
  if(cookie)await fetch(base+'/api/stop',{method:'POST',headers:{Cookie:cookie,Origin:base,'X-DLT-Request':'1'}});
  const until=Date.now()+10000;while(child.exitCode===null&&Date.now()<until)await new Promise(r=>setTimeout(r,100));
  if(child.exitCode===null){child.kill();throw new Error('Isolated backend did not shut down');}
 }
})().catch(e=>{console.error(e);process.exit(1)});
