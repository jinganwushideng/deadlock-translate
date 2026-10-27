const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),base='http://localhost:17868',key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim();let passed=0;
 function check(value,name){assert(value,name);passed++;}
 const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();const login=await fetch(session.url,{redirect:'manual'});const cookie=login.headers.get('set-cookie').split(';')[0];
 const headers={Cookie:cookie,Origin:base,'X-DLT-Request':'1','Content-Type':'application/json'};
 check((await fetch(base+'/api/diagnostics/export',{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:'{}'})).status===401,'export requires authenticated session');
 check((await fetch(base+'/api/diagnostics/export',{method:'POST',headers:{...headers,Origin:'https://evil.example'},body:'{}'})).status===403,'export validates origin');
 const before=await(await fetch(base+'/api/config',{headers})).json();check(before.settings.outgoingHotkey==='LAlt+Space','old settings get default hotkey');
 const changed={...before.settings,outgoingHotkey:'Ctrl+Enter',mode:'fast',llmKey:'FORBIDDEN_TEST_KEY',model:'FORBIDDEN_TEST_MODEL',llmBaseUrl:'https://forbidden.example/v1'};
 check((await fetch(base+'/api/config',{method:'PUT',headers,body:JSON.stringify(changed)})).status===200,'custom chord saves');
 check((await fetch(base+'/api/config',{method:'PUT',headers,body:JSON.stringify({...changed,outgoingHotkey:'Alt+F4'})})).status===400,'reserved chord refused');
 const req={session:'FORBIDDEN_CHAT_SESSION',seq:1,messages:[{id:'FORBIDDEN_RAW_JOB',text:"don't fight",sender:'FORBIDDEN_SENDER'}],poll:[],chatOpen:true,hotkeySeen:0,clientVersion:'0.6.4',logs:[{seq:1,code:'hotkey-captured',job:'FORBIDDEN_RAW_JOB',channel:'team'},{seq:2,code:'send-submitted',job:'FORBIDDEN_RAW_JOB',channel:'team'}]};
 const send=async body=>await(await fetch(base+'/game/'+key,{method:'POST',headers,body:JSON.stringify(body)})).json();
 const r=await send(req);check(r.logAck===2&&r.capabilities.includes('diagnostic-logs'),'game events acknowledged');check(r.config.outgoingHotkey==='Ctrl+Enter','game receives saved binding label');
 await send({...req,seq:2,serverId:r.serverId,messages:[]});
 const exported=await(await fetch(base+'/api/diagnostics/export',{method:'POST',headers,body:'{}'})).json();check(fs.existsSync(exported.file),'one-click export creates ZIP');
 const d=await(await fetch(base+'/api/diagnostics',{headers})).json();check(d.logs.available&&d.logs.maximumFiles===4&&d.logs.maximumFileBytes===1048576,'bounded logging is reported');
 check((await fetch(base+'/api/hotkey/reinitialize',{method:'POST',headers,body:'{}'})).status===409,'disabled native-hook fixture reports unavailability');
 fs.writeFileSync(path.join(root,'artifacts/diagnostics-http-tests.json'),JSON.stringify({passed,archive:exported.file,scope:'isolated backend; no real translation API or game input'},null,2));console.log({passed,archive:exported.file});
})().catch(e=>{console.error(e);process.exit(1);});
