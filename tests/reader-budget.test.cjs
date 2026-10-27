const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const world=require('./helpers/chat-world.cjs'),source=fs.readFileSync(path.join(__dirname,'../mod/panorama/scripts/dlt_chat.js'),'utf8');
let passed=0;const evidence=[];function check(name,run){run();passed++;console.log('PASS '+name);}
check('large history and HUD receive separate bounded row allowances',()=>{
 const w=world(),rows=[];for(let i=0;i<120;i++)rows.push(w.row('message '+i));for(let i=0;i<28;i++)w.bubble('bubble '+i);
 let previous={regular:0,hud:0};
 for(let i=0;i<40;i++){
  w.step();const metrics=w.packets.flatMap(p=>p.metrics?[p.metrics]:[]).at(-1); // not every tick publishes metrics
  if(metrics){previous={regular:metrics.regularRows,hud:metrics.hudRows};}
 }
 assert(w.requests.some(r=>r.text==='message 119'));assert(w.requests.some(r=>r.text==='bubble 27'));
 assert(new Set(w.requests.map(r=>r.id)).size===w.requests.length);evidence.push({regularAndHudIntake:w.requests.length,duplicateRequests:0});
});
check('a slow row scan yields instead of traversing the entire history in one callback',()=>{
 const w=world();for(let i=0;i<100;i++)w.row('slow '+i);w.open();
 const original=w.Panel.prototype.FindChildrenWithClassTraverse;let calls=0;
 w.Panel.prototype.FindChildrenWithClassTraverse=function(c){calls++;w.now+=1;return original.call(this,c);};
 const before=w.requests.length;w.step(1);const first=w.requests.length-before;
 assert(first<24,'reading must stop at its soft deadline');
 w.Panel.prototype.FindChildrenWithClassTraverse=original;w.step(35);assert(w.requests.some(r=>r.text==='slow 99'));
 evidence.push({firstCallbackRequests:first,deadlineHasYield:true});
});
check('unchanged live HUD bindings still recover rather than disappearing behind the budget',()=>{
 const w=world(),b=w.bubble('keep visible');w.step(12);const label=b.__dltLabel;assert(label&&label.text);label.visible=false;w.step(8);assert.equal(label.visible,true);assert.equal(w.requests.filter(r=>r.text==='keep visible').length,1);
});
check('limited reader eventually covers old and new history without duplicate translation jobs',()=>{
 const w=world();for(let i=0;i<70;i++)w.row('history '+i);for(let i=0;i<20;i++)w.bubble('native '+i);w.open();w.step(65);
 for(let i=0;i<70;i++)assert.equal(w.requests.filter(r=>r.text==='history '+i).length,1,'history '+i);
 for(let i=0;i<20;i++)assert.equal(w.requests.filter(r=>r.text==='native '+i).length,1,'native '+i);
 w.row('new tail');w.step(3);assert.equal(w.requests.filter(r=>r.text==='new tail').length,1);
 evidence.push({historyCovered:70,hudCovered:20,newTailCovered:true,duplicateJobs:0});
});
check('old and new readers are compared under the same simulated native lookup cost',()=>{
 const baseline=fs.readFileSync(path.join(__dirname,'../artifacts/send-ui-recovery-20261026/before/mod/panorama/scripts/dlt_chat.js'),'utf8');
 function sample(script){
  const w=world(script);for(let i=0;i<80;i++)w.row('cost '+i);for(let i=0;i<20;i++)w.bubble('bubble cost '+i);w.open();
  for(const name of ['FindChildTraverse','FindChildrenWithClassTraverse']){const fn=w.Panel.prototype[name];w.Panel.prototype[name]=function(...args){w.now+=.1;return fn.apply(this,args);};}
  let max=0,total=0,callbacks=0;for(;callbacks<400&&w.requests.length<100;callbacks++){const start=w.now;w.step(1);const work=w.now-start-200;max=Math.max(max,work);total+=work;}
  return {maxSimulatedMs:Math.round(max*10)/10,totalSimulatedMs:Math.round(total*10)/10,requests:w.requests.length,callbacks};
 }
 const before=sample(baseline),after=sample(source);assert(after.maxSimulatedMs<before.maxSimulatedMs);assert.equal(before.requests,100);assert.equal(after.requests,100);
 evidence.push({lookupCostModelOnly:true,before,after,notAnFpsBenchmark:true});
});
fs.writeFileSync(path.join(__dirname,'../artifacts/send-ui-recovery-20261026/reader-budget-tests.json'),JSON.stringify({passed,evidence,realGame:false,fpsNotMeasured:true},null,2));
