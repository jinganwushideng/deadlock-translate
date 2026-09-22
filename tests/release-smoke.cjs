const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.join(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version;
 const base='http://localhost:17868',key=fs.readFileSync(path.join(root,'dist/DeadlockTranslate-'+version+'/pairing.key'),'utf8').trim();
 let health;const readyDeadline=Date.now()+15000;
 while(!health&&Date.now()<readyDeadline){try{health=await(await fetch(base+'/health',{signal:AbortSignal.timeout(1000)})).json();}catch{await new Promise(r=>setTimeout(r,100));}}
 assert(health,'packaged backend did not become ready');assert.equal(health.version,version);assert(health.features.includes('native-chat-submit'));
 const s=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();const login=await fetch(s.url,{redirect:'manual'});const cookie=login.headers.get('set-cookie').split(';')[0];
 const headers={Cookie:cookie,Origin:base,'X-DLT-Request':'1','Content-Type':'application/json'};
 const config=await(await fetch(base+'/api/config',{headers})).json();assert.equal(config.settings.translateOwn,false);assert.equal(config.settings.incomingScope,'all_players');assert.equal(config.settings.outgoingDisplayMode,'translation');
 const metrics={scans:1,rows:12,submitted:1,rendered:0,bridges:1,errors:0,maxScanMs:1,regularRows:3,hudRows:4,lobbyRows:5,skippedSelf:1,skippedUnknown:2,sendAttempts:0,sendErrors:0};
 const exchange=async messages=>{const r=await fetch(base+'/game/'+key,{method:'POST',headers,body:JSON.stringify({session:'release-fixture',seq:1,messages,poll:['one'],metrics})});assert.equal(r.status,200);return r.json();};
 let data=await exchange([{id:'one',text:"don't fight"}]);for(let i=0;i<40&&!data.results.some(x=>x.text==='别打');i++){await new Promise(r=>setTimeout(r,25));data=await exchange([]);}assert(data.results.some(x=>x.text==='别打'));assert.equal(data.config.translateOwn,false);assert.equal(data.config.incomingScope,'all_players');assert.equal(data.config.outgoingDisplayMode,'translation');assert(data.capabilities.includes('outgoing-display-mode'));assert.equal(data.hotkeyAvailable,false);
 const status=await(await fetch(base+'/api/status',{headers})).json();assert.equal(status.gameEvidence.metrics.hudRows,4);assert.equal(status.gameEvidence.metrics.skippedUnknown,2);
 assert(data.capabilities.includes('outgoing-party-channel'));
 assert.equal(config.settings.fastConcurrency,4);assert.equal(config.settings.llmConcurrency,4);assert.equal(config.settings.outgoingConcurrency,3);assert.equal(config.settings.outgoingQueueLimit,12);assert.equal(data.config.outgoingQueueLimit,12);
 const result={version,passed:17,packagedExe:true,at:new Date().toISOString(),scope:'isolated HTTP fixture; native hotkey disabled; no real game messages'};fs.writeFileSync(path.join(root,'artifacts/release-smoke.json'),JSON.stringify(result,null,2));console.log(result);
})().catch(e=>{console.error(e);process.exit(1);});
