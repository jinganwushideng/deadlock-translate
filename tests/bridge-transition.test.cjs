const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim(),base='http://localhost:17868';
 const response=await fetch(base+'/bridge/'+key);assert.equal(response.status,200);const html=await response.text(),script=html.match(/<script>([\s\S]*)<\/script>/)[1];
 const data=(seq,session)=>({seq,session,messages:[],poll:[]}),location={hash:'#'+encodeURIComponent(JSON.stringify(data(1,'old-scene'))),search:''},document={title:'DLT:ready'},events={},calls=[];
 vm.runInNewContext(script,{location,document,URLSearchParams,decodeURIComponent,JSON,addEventListener:(name,fn)=>events[name]=fn,fetch:(url,options)=>new Promise((resolve,reject)=>calls.push({body:JSON.parse(options.body),resolve,reject}))});
 let passed=0;const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);},flush=()=>new Promise(r=>setImmediate(r));
 check(calls.length===1&&calls[0].body.session==='old-scene','initial request begins once');
 for(let i=2;i<=20;i++){location.hash='#'+encodeURIComponent(JSON.stringify(data(i,'new-scene')));events.hashchange();}
 check(calls.length===1,'busy browser bridge does not launch parallel duplicate requests');
 calls[0].resolve({ok:true,json:async()=>data(1,'old-scene')});await flush();
 check(calls.length===2&&calls[1].body.seq===20&&calls[1].body.session==='new-scene','after transition the latest fragment drains without reloading HTML');
 calls[1].resolve({ok:true,json:async()=>data(20,'new-scene')});await flush();events.hashchange();await flush();
 check(calls.length===2&&JSON.parse(document.title.slice(4)).session==='new-scene','completed fragment is not submitted repeatedly');
 location.hash='#'+encodeURIComponent(JSON.stringify(data(21,'new-scene')));events.hashchange();calls[2].reject(new Error('PRIVATE_ERROR_MUST_NOT_ENTER_GAME_TITLE'));await flush();
 check(document.title==='DLT:'+JSON.stringify({error:'bridge-request-failed'}),'request errors cannot leak arbitrary text into game title');
 const r=await fetch(base+'/game/'+key,{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify(data(22,'scope-check'))});
 check(r.status===200&&(await r.json()).session==='scope-check','real backend response carries explicit scene-session ownership');
 fs.writeFileSync(path.join(root,'artifacts/bridge-transition-tests.json'),JSON.stringify({passed,realTranslationApi:false,realGame:false},null,2));
})().catch(e=>{console.error(e);process.exit(1);});
