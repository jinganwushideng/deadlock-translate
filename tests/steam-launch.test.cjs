const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict'),{spawn,execFileSync}=require('node:child_process');
const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'))).version;
const exe=path.join(root,'dist','DeadlockTranslate-'+version,'DeadlockTranslate.exe');
const dir=path.join(root,'artifacts','steam-launch-'+Date.now()),game=path.join(root,'artifacts','Steam fixture 中文','SteamLaunchFixture.exe'),base='http://localhost:17871';
const sleep=ms=>new Promise(r=>setTimeout(r,ms)),children=[];let passed=0;
const alive=p=>p.exitCode===null&&p.signalCode===null;
const start=(args,env={})=>{const p=spawn(exe,args,{cwd:path.dirname(game),windowsHide:true,stdio:'ignore',env:{...process.env,...env}});children.push(p);return p;};
async function health(){try{return await(await fetch(base+'/health',{signal:AbortSignal.timeout(500)})).json();}catch{return null;}}
async function until(fn,ms=20000){const end=Date.now()+ms;while(Date.now()<end){const r=await fn();if(r)return r;await sleep(200);}throw new Error('Timed out');}
function check(value,label){assert(value,label);passed++;console.log('PASS '+label);}
(async()=>{
 assert.equal(await health(),null);fs.mkdirSync(dir,{recursive:true});
 const backendArgs=['--port','17871','--data-dir',dir,'--game-exe',game,'--no-hotkey','--no-shell'];
 const gameArgs=['--launch','--port','9999','with spaces','quote"here','C:\\ends\\','& literal','中文'];
 for(const reuse of [false,true]){
  const output=path.join(dir,reuse?'reuse.json':'fresh.json');
  let existing;if(reuse){existing=start(backendArgs);await until(health);}
  const wrapper=start([...backendArgs,'--steam-game',game,...gameArgs],{SteamAppId:'1422450',DLT_TEST_OUTPUT:output,DLT_TEST_URL:base});
  await until(()=>fs.existsSync(output));const report=JSON.parse(fs.readFileSync(output));
  check(JSON.stringify(report.arguments)===JSON.stringify(gameArgs),'all game arguments forwarded exactly ('+reuse+')');
  check(report.ready&&report.steamAppId==='1422450','backend is ready and Steam environment inherited ('+reuse+')');
  check(report.workingDirectory.toLowerCase()===path.dirname(game).toLowerCase()&&report.priority==='Normal','working directory and normal game priority preserved ('+reuse+')');
  check((await health()).followingGame,'automatic exit is armed ('+reuse+')');
  if(reuse){await until(()=>!alive(wrapper),5000);check(alive(existing),'transient second launcher exits after handing off');}
  await until(()=>!alive(existing||wrapper),25000);check(!await health(),'backend exits after launched game ends ('+reuse+')');
 }
 // Exercise delegated launch without UAC or the real game. This is the same route a
 // normal Steam wrapper uses when a matching elevated backend already exists.
 const output=path.join(dir,'delegated.json');const existing=start(backendArgs,{DLT_TEST_OUTPUT:output,DLT_TEST_URL:base});await until(health);
 const owner=execFileSync('powershell.exe',['-NoProfile','-Command',"$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;$taskSha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($taskSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskSid)))).Replace('-','').ToLowerInvariant()}finally{$taskSha.Dispose()}"],{windowsHide:true,encoding:'utf8'}).trim();
 const scopeHash=p=>crypto.createHash('sha256').update(path.resolve(p).replace(/[\\/]+$/,'').toUpperCase()).digest('hex');
 const scope={owner,data:scopeHash(dir),runtime:scopeHash(path.dirname(exe))};
 const key=fs.readFileSync(path.join(path.dirname(exe),'pairing.key'),'utf8').trim();
 const launchBody={scope,game:{executable:game,arguments:gameArgs,workingDirectory:path.dirname(game)}};
 const post=body=>fetch(base+'/control/'+key+'/launch-game',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 check((await post({...launchBody,scope:{...scope,owner:'wrong'}})).status===403,'delegated game launch rejects another user scope');
 check((await post({...launchBody,game:{...launchBody.game,executable:process.execPath}})).status===500,'delegated game launch refuses an executable outside installation');
 check((await post(launchBody)).status===200,'verified backend starts the exact synthetic game through delegated route');
 await until(()=>fs.existsSync(output));const report=JSON.parse(fs.readFileSync(output));
 check(JSON.stringify(report.arguments)===JSON.stringify(gameArgs)&&report.ready&&report.priority==='Normal'&&report.steamAppId==='1422450','delegated launch preserves arguments, game priority and known Steam app ID');
 await until(()=>!alive(existing),25000);check(!await health(),'delegated launch follows game exit without an extra watcher');
 fs.writeFileSync(path.join(root,'artifacts','steam-launch-tests.json'),JSON.stringify({version,passed,at:new Date().toISOString(),scope:'actual Windows processes and packaged EXE; synthetic Steam command/environment, no real game launch'},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>{for(const p of children)if(alive(p))p.kill();});
