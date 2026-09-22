const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'))).version;
const exe=path.join(root,'dist','DeadlockTranslate-'+version,'DeadlockTranslate.exe');
const dir=path.join(root,'artifacts','lifecycle-'+Date.now()),game=path.join(dir,'dlt-game-fixture.exe'),base='http://localhost:17869';
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const children=[];let passed=0;
const start=(file,args)=>{const p=spawn(file,args,{windowsHide:true,stdio:'ignore'});children.push(p);return p;};
const alive=p=>p.exitCode===null&&p.signalCode===null;
async function health(){try{return await(await fetch(base+'/health',{signal:AbortSignal.timeout(500)})).json();}catch{return null;}}
async function until(fn,ms=20000){const end=Date.now()+ms;while(Date.now()<end){const result=await fn();if(result)return result;await sleep(250);}throw new Error('Timed out');}
function check(value,label){assert(value,label);passed++;console.log('PASS '+label);}
(async()=>{
 assert.equal(await health(),null,'fixture port must be free');fs.mkdirSync(dir,{recursive:true});
 fs.copyFileSync(path.join(process.env.SystemRoot,'System32','PING.EXE'),game);
 const args=['--port','17869','--data-dir',dir,'--game-exe',game,'--no-hotkey'];
 let fixture=start(game,['-t','127.0.0.1']);
 const backend=start(exe,['--follow-game',...args]);
 let h=await until(health);check(h.followingGame&&h.version===version,'packaged backend runs with linked exit');
 await sleep(2500);fixture.kill();await sleep(3000);
 check(!!await health(),'short process gap does not immediately shut down the backend');
 await until(()=>!alive(backend));check(!await health(),'confirmed game exit stops the only backend process');
 const existing=start(exe,args);h=await until(health);check(!h.followingGame,'settings-only backend remains independent');
 fixture=start(game,['-t','127.0.0.1']);
 const duplicate=start(exe,['--follow-game',...args]);
 await until(async()=>{const v=await health();return v?.followingGame;});await until(()=>!alive(duplicate),5000);
 check(alive(existing),'second launcher attaches exit handling to the existing process then exits');
 const key=fs.readFileSync(path.join(root,'dist','DeadlockTranslate-'+version,'pairing.key'),'utf8').trim();
 const forbidden=await fetch(base+'/control/'+key+'/follow-game',{method:'POST',headers:{Origin:'https://invalid.example'}});
 check(forbidden.status===404,'foreign-origin lifecycle request rejected');
 const invalid=await fetch(base+'/control/wrong-key/follow-game',{method:'POST'});
 check(invalid.status===404,'invalid pairing cannot arm game exit');
 await sleep(2500);fixture.kill();await until(()=>!alive(existing));
 check(!await health(),'attached backend also exits after the game ends');
 await sleep(2500);check(!await health(),'no extra monitor restarts the stopped backend');
 fs.writeFileSync(path.join(root,'artifacts','lifecycle-tests.json'),JSON.stringify({version,passed,at:new Date().toISOString(),scope:'packaged EXE and renamed local ping process; no real game, registry, startup or persistent watcher'},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>{
 for(const child of children)if(alive(child))child.kill();
});
