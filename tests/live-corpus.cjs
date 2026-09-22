if (process.env.DLT_ALLOW_REAL_API !== '1') { throw new Error('Real-provider test disabled. Use an isolated backend and explicitly set DLT_ALLOW_REAL_API=1.'); }
// Uses an already-configured backend. Never reads or writes provider credentials.
const fs=require('node:fs'),path=require('node:path');
(async()=>{
 const port=process.argv[2]||'17867',limit=Number(process.argv[3]||'12'),base='http://localhost:'+port;
 const key=fs.readFileSync(path.join(__dirname,'../artifacts/pairing.key'),'utf8').trim();
 const session=await(await fetch(base+'/control/'+key+'/session',{method:'POST'})).json();
 const response=await fetch(session.url,{redirect:'manual'});const cookie=response.headers.get('set-cookie').split(';')[0];
 const headers={Cookie:cookie,Origin:base,'X-DLT-Request':'1','Content-Type':'application/json'};
 const cfg=await(await fetch(base+'/api/config',{headers})).json();
 if(!cfg.hasLlmKey)throw new Error('No configured model credential');
 const ids=process.argv[4]?process.argv[4].split(',').map(Number):null;
 const corpus=fs.readFileSync(path.join(__dirname,'corpus.tsv'),'utf8').trim().split(/\r?\n/).slice(1).map((line,i)=>{const [source,reference,category]=line.split('\t');return {id:i+1,source,reference,category};}).filter(item=>!ids||ids.includes(item.id)).slice(0,limit);
 const results=[];let index=0;
 async function worker(){while(index<corpus.length){const item=corpus[index++];const started=Date.now();try{const r=await fetch(base+'/api/test',{method:'POST',headers,body:JSON.stringify({provider:'quality',text:item.source}),signal:AbortSignal.timeout(95000)});const data=await r.json();results.push({...item,status:r.status,...data,wallMs:Date.now()-started});}catch(e){results.push({...item,status:0,error:e.message,wallMs:Date.now()-started});}fs.writeFileSync(path.join(__dirname,'../artifacts/live-corpus-progress.json'),JSON.stringify(results,null,2));if(results.length%5===0||results.length===corpus.length)console.log('Completed '+results.length+'/'+corpus.length);}}
 await Promise.all([worker(),worker()]);results.sort((a,b)=>a.id-b.id);
 const successful=results.filter(r=>r.status===200),latencies=successful.map(r=>r.elapsedMs).sort((a,b)=>a-b);
 const report={at:new Date().toISOString(),model:cfg.settings.model,endpoint:cfg.settings.llmBaseUrl,count:results.length,successful:successful.length,p50Ms:latencies[Math.floor(latencies.length*.5)]??null,p95Ms:latencies[Math.min(latencies.length-1,Math.floor(latencies.length*.95))]??null,qualityReview:'pending developer review; not independent human acceptance',results};
 fs.writeFileSync(path.join(__dirname,'../artifacts/live-corpus-'+(ids?'retry-':'')+limit+'.json'),JSON.stringify(report,null,2));console.log(JSON.stringify({...report,results:limit<=12?results:undefined},null,2));
})().catch(e=>{console.error(e.message);process.exit(1)});
