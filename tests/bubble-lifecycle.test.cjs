const nativeBubble=require('./helpers/native-bubble.cjs'),hudLabel=nativeBubble.translated;
// Synthetic native HUD lifecycle; no game, cloud requests or per-frame full-root scan.
const vm=require('node:vm'),fs=require('node:fs'),assert=require('node:assert/strict'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../mod/panorama/scripts/dlt_chat.js'),'utf8');
let now=100000,tick,requests=[],polls=[],visits=0,sequence=0,revision=1,responseText='译文',hold=false;
class Panel{
 constructor(id,parent,cls=''){this.id=id;this.parent=parent;this.children=[];this.alive=true;this.text='';this.style={};this.cls=new Set(cls.split(' '));if(parent)parent.children.push(this);}
 IsValid(){return this.alive;} GetParent(){return this.parent;} GetChildCount(){visits++;return this.children.length;} GetChild(i){visits++;return this.children[i];}
 BHasClass(c){return this.cls.has(c);} AddClass(c){this.cls.add(c);}
 FindChildTraverse(id){visits++;if(this.id===id)return this;for(const c of this.children){const p=c.FindChildTraverse(id);if(p)return p;}return null;}
 FindChildrenWithClassTraverse(c){visits++;return this.children.flatMap(p=>[...(p.BHasClass(c)?[p]:[]),...p.FindChildrenWithClassTraverse(c)]);}
 SetURL(url){if(!url.includes('#')&&!url.includes('?')){this.title='DLT:ready';return;}const q=JSON.parse(decodeURIComponent(url.split('#')[1]||url.split('?d=')[1]));sequence=q.seq;requests.push(...q.messages);polls.push(q);
  this.title='DLT:'+JSON.stringify({seq:q.seq,results:hold?[]:[...q.messages,...q.poll.map(id=>({id}))].map(m=>({id:m.id,text:responseText,revision,stage:revision===1?'fast':'quality',done:revision>1})),config:{enabled:true,incomingScope:'all_players',scanIntervalMs:200,transport:'fragment'},configuredLlm:true});}
}
nativeBubble.install(Panel);
const mountNative=host=>nativeBubble.mount((id,parent,cls)=>new Panel(id,parent,cls),host);
const root=new Panel('Root'),ctx=new Panel('Chat',root),history=new Panel('ChatMessages',ctx),bridge=new Panel('DltBridge',ctx),hud=new Panel('Team1Chat',root);let messages=new Panel('Messages',hud);
new Panel('DltMenuMount',new Panel('SubOptions',root));new Panel('ChatInput',ctx);new Panel('ChatTargetLabel',ctx);
for(let i=0;i<5000;i++)new Panel('unrelated'+i,root);
function destroy(p){p.alive=false;for(const c of [...p.children])destroy(c);if(p.parent)p.parent.children=p.parent.children.filter(c=>c!==p);}
function row(text){const r=new Panel('',history,'ChatMessage'),body=new Panel('',r,'MessageBody'),src=new Panel('MessageSource',body);new Panel('',src,'ChannelName').text='[ALLIES]';new Panel('',src,'SenderName').text='peer';new Panel('',new Panel('MessageContents',body)).text=text;return r;}
function bubble(text,container=messages){const r=new Panel('',container,'ChatMessage'),contents=new Panel('MessageContents',r),shape=new Panel('',contents,'ChatBubble'),c=new Panel('',shape,'TextContainer'),wrap=new Panel('',c,'MessageWrapper');new Panel('MessageText',wrap).text=text;mountNative(contents);return r;}
const sandbox={Date:{now:()=>now},Math,JSON,encodeURIComponent,String,Players:{GetLocalPlayer:()=>0,GetTeam:()=>2},$:{GetContextPanel:()=>ctx,Schedule:(_,fn)=>{tick=fn;return 1;},CreatePanel:(_,parent,id)=>new Panel(id,parent)}};
vm.runInNewContext(source,sandbox);function step(n=1){for(let i=0;i<n;i++){now+=200;tick();}}
let passed=0;function check(f,name){f();passed++;console.log('PASS '+name);}
step(6);
const a=row('first message'),b=bubble('first message');step(6);
check(()=>assert.equal(requests.filter(m=>m.text==='first message').length,1),'history and bubble share one task');
check(()=>assert.equal(hudLabel(b).text,responseText),'new MessageWrapper nested bubble translated');
const beforeReplace=now;destroy(messages);messages=new Panel('Messages',hud);const c=bubble('replacement container');step(5);
check(()=>assert(hudLabel(c)),'rebuilt Messages container repaired within one second');
check(()=>assert(now-beforeReplace<=1000),'replacement test does not wait for ten second discovery');
const label=hudLabel(c);destroy(label);const beforeRequests=requests.length;step(4);check(()=>assert.equal(hudLabel(c),null),'missing native template is not replaced with a late dynamic child');mountNative(c.FindChildTraverse('MessageContents'));step(4);
check(()=>assert.equal(hudLabel(c)?.text,responseText),'deleted translator label restored from cached completed text');
check(()=>assert.equal(requests.length,beforeRequests),'label recovery does not submit another cloud task');
hold=true;const recycled=bubble('message alpha');step(4);recycled.FindChildTraverse('MessageText').text='message beta';step(4);hold=false;step(4);
check(()=>assert.equal(hudLabel(recycled)?.text,responseText),'reused native row renders only current task');
const recycledLabel=hudLabel(recycled);recycledLabel.text='';step(3);
check(()=>assert.equal(hudLabel(recycled)?.text,responseText),'cleared label content restored');
revision=2;responseText='精翻';step(5);
check(()=>assert.equal(hudLabel(recycled)?.text,'精翻'),'quality revision replaces fast text');
const gone=bubble('expired before response');hold=true;step(3);destroy(gone);const childCount=messages.children.length;hold=false;step(5);
check(()=>assert.equal(messages.children.length,childCount),'late result never resurrects expired bubble');
hold=true;const faded=bubble('native fading message');step(3);faded.AddClass('Expired');hold=false;step(5);
check(()=>assert.equal(hudLabel(faded),null),'native Expired class prevents late label creation');
const stale=bubble('previous text');hold=true;step(3);stale.FindChildTraverse('MessageText').text='new current text';hold=false;
step(1);check(()=>assert.equal(hudLabel(stale),null),'old response cannot paint a row whose native text changed');step(5);
check(()=>assert(hudLabel(stale)),'changed row eventually renders its current translation');
const live=bubble('still live'),nativeText=live.FindChildTraverse('MessageText');step(5);
check(()=>assert.equal(nativeText.text,'still live'),'native original text preserved');
check(()=>assert.equal(hudLabel(live).html,false),'translated output remains plain text');
check(()=>assert(hudLabel(live).style.color),'HUD style set on owned label across native stylesheet scopes');
const recovered=hudLabel(live);live.children=live.children.filter(c=>c!==recovered.parent);recovered.parent.parent=null;
const contents=new Panel('MessageContents',live),wrapper=new Panel('',contents,'MessageWrapper');new Panel('MessageText',wrapper).text='still live';mountNative(contents);step(4);
check(()=>assert(hudLabel(live)&&hudLabel(live)!==recovered),'detached valid native children get a current translation label');
// An engine can retire contents in place instead of deleting/detaching the subtree.
const retained=bubble('retained native subtree');step(5);
const retiredContents=retained.FindChildTraverse('MessageContents'),retiredLabel=hudLabel(retained);
retiredContents.id='RetiredContents';retiredContents.visible=false;destroy(retiredContents.FindChildTraverse('MessageText'));
const activeContents=new Panel('MessageContents',retained),activeWrapper=new Panel('',activeContents,'MessageWrapper');new Panel('MessageText',activeWrapper).text='retained native subtree';mountNative(activeContents);
const requestsBeforeHostRepair=requests.length;step(4);
check(()=>assert.equal(hudLabel(activeContents)?.text,responseText),'retained hidden contents cannot keep translation bound to obsolete host');
check(()=>assert.equal(retiredLabel.text,''),'obsolete translator label is cleared without deleting native panels');
check(()=>assert.equal(requests.length,requestsBeforeHostRepair),'native host replacement reuses completed task without another provider request');
const hidden=hudLabel(activeContents);hidden.visible=false;step(4);
check(()=>assert.equal(hidden.visible,true),'individually hidden translator label is restored while original bubble remains live');
hidden.style.visibility='collapse';hidden.style.opacity='0';step(4);
check(()=>assert.equal(hidden.style.visibility,'visible'),'collapsed own label recovers without changing parent visibility');
check(()=>assert.equal(hidden.style.opacity,'0.9'),'own label opacity recovers to configured default without cancelling native row fade');
check(()=>assert.equal(retiredContents.visible,false),'native retired contents keep their visibility and position');
root.children=root.children.filter(p=>p!==hud);hud.parent=null;const replacementHud=new Panel('Team1Chat',root),replacementMessages=new Panel('Messages',replacementHud),newBubble=bubble('replacement HUD',replacementMessages);step(5);
check(()=>assert(hudLabel(newBubble)),'detached valid HUD is rediscovered with bounded delay');
visits=0;step(8);
check(()=>assert(visits<5000),'normal bubble scans stay inside cached HUD roots');
const normalVisits=visits;
const typing=new Panel('',replacementMessages,'ChatMessage');step(3);
check(()=>assert.equal(requests.filter(m=>!m.text).length,0),'typing indicator without MessageText is ignored');
const burst=[];for(let i=0;i<8;i++)burst.push(bubble('burst '+i,replacementMessages));step(12);
check(()=>assert(burst.every(p=>hudLabel(p))),'eight simultaneous live bubbles all render with bounded work');
const metrics=polls.filter(p=>p.metrics).at(-1)?.metrics;
check(()=>assert(metrics&&metrics.hudRebinds>=2&&metrics.hudRendered>0&&metrics.hudRecovered>0&&metrics.hudHostRepairs>0&&metrics.hudHiddenRepairs>0),'diagnostic counters distinguish intake rebinding rendering and repair');
fs.writeFileSync(path.join(__dirname,'../artifacts/bubble-lifecycle-tests.json'),JSON.stringify({passed,normalScanVisits:normalVisits,gameTested:false,cloudUsed:false},null,2));
