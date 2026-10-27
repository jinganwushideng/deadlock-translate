const nativeBubble=require('./helpers/native-bubble.cjs'),hudLabel=nativeBubble.translated;
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),world=require('./helpers/chat-world.cjs');
let passed=0,failed=0;
function check(name,fn){try{fn();passed++;console.log('PASS '+name);}catch(e){failed++;console.error('FAIL '+name+': '+e.stack);}}
function logs(w){return w.packets.flatMap(p=>p.logs||[]);}
function feed(w){return w.root.FindChildTraverse('DltIncomingFeed');}
function shown(w){return (feed(w)?.children||[]).filter(p=>p.visible);}
function independent(w,seconds=8,lines=3){Object.assign(w.config,{incomingDisplayMode:'independent',incomingFeedSeconds:seconds,incomingFeedLines:lines});w.refresh();}
check('temporarily hidden native HUD retries without classifying it as expired',()=>{
 const w=world();w.ready=()=>w.now+1600;const b=w.bubble('temporary visibility');w.step(3);w.hud.visible=false;w.step(12);
 assert(!hudLabel(b));assert(logs(w).some(e=>e.code==='hud-wait'&&e.reason==='hidden'));assert(!logs(w).some(e=>e.code==='hud-retired'));
 w.hud.visible=true;w.step(8);assert(hudLabel(b));assert(logs(w).some(e=>e.code==='hud-restored'));assert.equal(w.requests.length,1);
});
check('duplicate native contents IDs select the live source ancestry',()=>{
 const w=world(),b=w.bubble('duplicate native source');w.step(5);const old=b.FindChildTraverse('MessageContents'),oldLabel=hudLabel(b);old.visible=false;
 const live=new w.Panel('MessageContents',b),wrap=new w.Panel('',live);new w.Panel('MessageText',wrap).text='duplicate native source';w.mountNative(live);w.step(6);
 assert(hudLabel(live));assert.equal(oldLabel.text,'');assert.equal(old.visible,false);assert.equal(w.requests.length,1);
});
check('native Expired state remains terminal and receives a specific reason',()=>{
 const w=world();w.ready=()=>w.now+1800;const b=w.bubble('really expired');w.step(3);b.AddClass('Expired');w.step(14);
 assert(!hudLabel(b));assert(logs(w).some(e=>e.code==='hud-retired'&&e.reason==='native-expired'));assert(!logs(w).some(e=>e.code==='hud-wait'));
});
check('a temporary missing native text child preserves the original task and retry',()=>{
 const w=world();w.ready=()=>w.now+1800;const b=w.bubble('temporary snippet rebuild');w.step(3);const text=b.FindChildTraverse('MessageText'),parent=text.parent;parent.children=parent.children.filter(p=>p!==text);text.parent=null;w.step(12);
 assert(logs(w).some(e=>e.code==='hud-wait'&&e.reason==='host-missing'));parent.children.push(text);text.parent=parent;w.step(8);assert(hudLabel(b));assert.equal(w.requests.length,1);
});
check('destroyed native panels are distinguished from native expiration',()=>{
 const w=world();w.ready=()=>w.now+1800;const b=w.bubble('panel destroyed');w.step(3);w.destroy(b);w.step(14);assert(logs(w).some(e=>e.code==='hud-retired'&&e.reason==='panel-gone'));
});
check('temporary retry work and transition logging stay bounded',()=>{
 const w=world();w.ready=()=>w.now+1400;w.bubble('bounded waiting');w.step(3);w.hud.visible=false;w.step(180);
 assert(logs(w).filter(e=>e.code==='hud-wait').length<=2);assert(logs(w).some(e=>e.code==='hud-retry-ended'&&e.reason==='retry-expired'));assert.equal(w.requests.length,1);
});
check('temporary hide cycles do not masquerade as repeated native container rebuilds',()=>{
 const w=world();w.bubble('stable native container');w.step(6);const before=logs(w).filter(e=>e.code==='hud-rebound').length;
 for(let i=0;i<8;i++){w.hud.visible=false;w.step(2);w.hud.visible=true;w.step(2);}
 assert.equal(logs(w).filter(e=>e.code==='hud-rebound').length,before);
});
check('independent feed displays a delayed result after the native bubble is destroyed',()=>{
 const w=world();independent(w);w.ready=()=>w.now+3000;const b=w.bubble('late translated text');w.step(3);w.destroy(b);w.step(20);
 assert.equal(shown(w).length,1);assert(shown(w)[0].FindChildTraverse('DltFeedText0').text.includes('late translated text'));assert.equal(w.messages.children.length,0);assert.equal(feed(w).hittest,false);assert.equal(feed(w).hittestchildren,false);
});
check('independent feed retains history translation and avoids duplicate native labels',()=>{
 const w=world();independent(w);const r=w.row('both surfaces'),b=w.bubble('both surfaces');w.step(10);
 assert(r.FindChildTraverse('DltTranslation'));assert(!hudLabel(b));assert.equal(shown(w).length,1);assert.equal(w.requests.length,1);
});
check('feed lifetime begins at result display rather than native arrival',()=>{
 const w=world();independent(w,3);w.ready=()=>w.now+10000;const b=w.bubble('slow response');w.step(3);w.destroy(b);w.step(52);
 assert.equal(shown(w).length,1);w.step(16);assert.equal(shown(w).length,0);
});
check('a quality update reuses a slot without extending its original lifetime',()=>{
 const w=world();independent(w,3);w.rewriteResults=rs=>rs.map(r=>({...r,text:'fast value',revision:1,stage:'fast',done:false}));w.bubble('updates');w.step(8);assert.equal(shown(w).length,1);
 const created=w.created;w.rewriteResults=rs=>rs.map(r=>({...r,text:'quality value',revision:2,stage:'quality',done:true}));w.step(4);assert.equal(w.created,created);assert.equal(shown(w)[0].FindChildTraverse('DltFeedText0').text,'quality value');
 w.step(13);assert.equal(shown(w).length,0);
});
check('a quality result cannot reopen a feed entry that already timed out',()=>{
 const w=world();independent(w,3);w.rewriteResults=rs=>rs.map(r=>({...r,text:'fast',revision:1,stage:'fast',done:false}));w.bubble('no replay');w.step(24);assert.equal(shown(w).length,0);
 w.rewriteResults=rs=>rs.map(r=>({...r,text:'quality',revision:2,stage:'quality',done:true}));w.step(8);assert.equal(shown(w).length,0);
});
check('feed bursts reuse ten slots, obey line limit and never replay displaced entries',()=>{
 const w=world();independent(w,8,2);for(let i=0;i<8;i++)w.bubble('burst feed '+i);w.step(16);
 assert.equal(feed(w).children.length,10);assert.equal(shown(w).length,2);const created=w.created;w.step(12);assert.equal(w.created,created);assert.equal(w.requests.length,8);assert(logs(w).some(e=>e.code==='feed-displaced'));
});
check('rebuilding the owned feed preserves the remaining display lifetime',()=>{
 const w=world();independent(w,3);w.bubble('rebuild own feed');w.step(8);assert.equal(shown(w).length,1);w.destroy(feed(w));w.step(4);assert.equal(shown(w).length,1);w.step(14);assert.equal(shown(w).length,0);
});
check('switching display mode clears only owned labels and retains original messages',()=>{
 const w=world(),b=w.bubble('mode switch');w.step(8);assert(hudLabel(b));independent(w);w.step(6);
 assert.equal(b.FindChildTraverse('MessageText').text,'mode switch');assert.equal(hudLabel(b),null);assert.equal(b.FindChildTraverse('DltBubbleTranslation').text,'');assert.equal(shown(w).length,1);
 w.config.incomingDisplayMode='native';w.refresh();w.step(6);assert.equal(shown(w).length,0);assert(hudLabel(b).text);
});
check('scene changes never replay old feed entries or old task results',()=>{
 const w=world();independent(w);w.bubble('old match');w.step(8);assert.equal(shown(w).length,1);w.match++;w.step(12);assert.equal(shown(w).length,0);
});
check('opening history alone does not replay old lines into the independent feed',()=>{
 const w=world();independent(w);w.row('history only');w.open();w.step(10);assert.equal(shown(w).length,0);
});
check('display diagnostics contain fixed states and never source message text',()=>{
 const w=world();independent(w);w.bubble('PRIVATE_MESSAGE_SENTINEL');w.step(10);const events=logs(w);assert(events.some(e=>e.code==='feed-written'));assert(!JSON.stringify(events).includes('PRIVATE_MESSAGE_SENTINEL'));
});
const result={passed,failed,realGame:false,cloudUsed:false};fs.writeFileSync(path.join(__dirname,'../artifacts/display-reliability-tests.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));if(failed)process.exitCode=1;
