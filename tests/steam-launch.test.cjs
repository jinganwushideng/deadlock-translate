const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),assert=require('node:assert/strict'),{spawn,execFileSync}=require('node:child_process');
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
 for(const [reuse,legacyFlag] of [[false,false],[true,false]]){
  const output=path.join(dir,legacyFlag?'legacy.json':reuse?'reuse.json':'fresh.json');
  let existing;if(reuse){existing=start(backendArgs);await until(health);}
  const wrapper=start([...backendArgs,...(legacyFlag?['--admin-compatible']:[]),'--steam-game',game,...gameArgs],{SteamAppId:'1422450',DLT_TEST_OUTPUT:output,DLT_TEST_URL:base});
  await until(()=>fs.existsSync(output));const report=JSON.parse(fs.readFileSync(output));
  check(JSON.stringify(report.arguments)===JSON.stringify(gameArgs),'all game arguments forwarded exactly ('+reuse+')');
  check(report.ready&&report.steamAppId==='1422450','backend is ready and Steam environment inherited ('+reuse+')');
  check(report.workingDirectory.toLowerCase()===path.dirname(game).toLowerCase()&&report.priority==='Normal','working directory and normal game priority preserved ('+reuse+')');
  check((await health()).followingGame,'automatic exit is armed ('+reuse+')');
  if(reuse){await until(()=>!alive(wrapper),5000);check(alive(existing),'transient second launcher exits after handing off');}
  await until(()=>!alive(existing||wrapper),25000);check(!await health(),'backend exits after launched game ends ('+reuse+')');
 }
 const blocked=path.join(dir,'explicit-admin-no-consent.json');
 const elevated=execFileSync('powershell.exe',['-NoProfile','-Command','([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'],{windowsHide:true,encoding:'utf8'}).trim()==='True';
 const admin=start([...backendArgs,'--admin-compatible','--steam-game',game,...gameArgs],{DLT_TEST_OUTPUT:blocked,DLT_TEST_URL:base});
 if(elevated){
  await until(()=>fs.existsSync(blocked));
  await until(()=>!alive(admin),25000);
  check(admin.exitCode===0,'an already elevated administrator launch does not request another UAC');
 }else{
  await until(()=>!alive(admin),10000);
  check(admin.exitCode===1&&!fs.existsSync(blocked)&&!await health(),'explicit administrator mode requires consent and cannot silently start in no-shell tests');
 }
 // The connection acts like an already running elevated backend. The fake server
 // can't start the game: only the original wrapper can pass this environment.
 const output=path.join(dir,'elevated-reuse.json');let followCount=0;let unexpected=0;
 const key=fs.readFileSync(path.join(path.dirname(exe),'pairing.key'),'utf8').trim();
 const server=http.createServer((req,res)=>{
  req.resume();res.setHeader('Content-Type','application/json');
  if(req.url==='/health')res.end(JSON.stringify({app:'DeadlockTranslate'}));
  else if(req.url==='/control/'+key+'/instance')res.end(JSON.stringify({version,elevated:true,handoff:false}));
  else if(req.url==='/control/'+key+'/follow-game'){followCount++;res.end('{}');}
  else{unexpected++;res.writeHead(404);res.end('{}');}
 });
 await new Promise((resolve,reject)=>server.once('error',reject).listen(17871,'127.0.0.1',resolve));
 try{
  const wrapper=start([...backendArgs,'--steam-game',game,...gameArgs],{SteamAppId:'1422450',DLT_TEST_OUTPUT:output,DLT_TEST_URL:base});
  await until(()=>fs.existsSync(output));const report=JSON.parse(fs.readFileSync(output));
  await until(()=>!alive(wrapper));
  check(wrapper.exitCode===0&&followCount===1&&unexpected===0,'elevated backend only follows the game; it never receives a launch request');
  check(JSON.stringify(report.arguments)===JSON.stringify(gameArgs)&&report.ready&&report.priority==='Normal'&&report.steamAppId==='1422450','game inherits the original wrapper environment and exact arguments when reusing an elevated backend');
 }finally{await new Promise(resolve=>server.close(resolve));}
 fs.writeFileSync(path.join(root,'artifacts','steam-launch-tests.json'),JSON.stringify({version,passed,at:new Date().toISOString(),scope:'actual Windows processes and packaged EXE; synthetic Steam command/environment, no real game launch'},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>{for(const p of children)if(alive(p))p.kill();});
