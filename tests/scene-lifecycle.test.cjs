// Native scene lifetimes, not a screenshot/FPS or real GPU crash reproduction.
const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const source=fs.readFileSync(path.join(__dirname,'../mod/panorama/scripts/dlt_chat.js'),'utf8');let passed=0;
function world(){
 const w={now:100000,match:1,tasks:new Map(),global:new Map(),local:[],serial:0,urls:[],packets:[],invalidReads:0,hotkey:0,transport:'fragment'};
 class Panel {
  constructor(type,id,parent,cls=''){Object.assign(this,{type,id,parent,children:[],text:'',style:{},alive:true,cls:new Set(cls.split(' ')),focused:false});if(parent)parent.children.push(this);}
  IsValid(){return this.alive&&(!this.parent||this.parent.IsValid());}
  guard(){if(!this.IsValid()){w.invalidReads++;throw Error('invalid native panel access');}}
  GetParent(){this.guard();return this.parent;}
  GetChildCount(){this.guard();return this.children.filter(c=>c.IsValid()).length;}
  GetChild(i){this.guard();return this.children.filter(c=>c.IsValid())[i];}
  BHasClass(c){this.guard();return this.cls.has(c);} AddClass(c){this.guard();this.cls.add(c);} BHasKeyFocus(){this.guard();return this.focused;}
  FindChildTraverse(id){this.guard();if(this.id===id)return this;for(const c of this.children){if(!c.IsValid())continue;const p=c.FindChildTraverse(id);if(p)return p;}return null;}
  FindChildrenWithClassTraverse(c){this.guard();return this.children.filter(x=>x.IsValid()).flatMap(x=>[...(x.cls.has(c)?[x]:[]),...x.FindChildrenWithClassTraverse(c)]);}
  SetParent(){throw Error('engine-owned nodes must never be reparented');}
  SetURL(url){this.guard();w.urls.push({url,at:w.now,panel:this});if(!url.includes('#')&&!url.includes('?')){this.title='DLT:ready';return;}
   const data=JSON.parse(decodeURIComponent(url.split('#')[1]||url.split('?d=')[1]));w.packets.push(data);
   this.title='DLT:'+JSON.stringify({seq:data.seq,session:data.session,serverId:'fixture-server',results:data.messages.map(m=>({id:m.id,text:'译文',revision:1,done:true})),outgoing:[],hotkey:w.hotkey>data.hotkeySeen?w.hotkey:0,logAck:data.logs.at(-1)?.seq||0,capabilities:['diagnostic-logs','outgoing-party-channel'],config:{enabled:true,incomingScope:'all_players',transport:w.transport,scanIntervalMs:200,outgoingEnabled:true,fontSize:17}});
  }
 }
 w.Panel=Panel;w.root=new Panel('Root','Root');const sub=new Panel('Panel','SubOptions',w.root);new Panel('Panel','DltMenuMount',sub);
 w.createChat=()=>{const c=new Panel('CitadelChat','Chat',w.root);new Panel('Panel','ChatMessages',c);new Panel('HTML','DltBridge',c);new Panel('TextEntry','ChatInput',c);new Panel('Label','ChatTargetLabel',c);return c;};
 w.boot=c=>{const dollar={GetContextPanel:()=>c,Msg:()=>{},Schedule:(seconds,fn)=>{const id=++w.serial;w.tasks.set(id,{at:w.now+seconds*1000,fn});return id;},CancelScheduled:id=>w.tasks.delete(id),CreatePanel:(type,parent,id)=>new Panel(type,id,parent),RegisterForUnhandledEvent:(name,fn)=>{const id=++w.serial;w.global.set(id,{name,fn});return id;},UnregisterForUnhandledEvent:(name,id)=>w.global.delete(id),RegisterEventHandler:(name,panel,fn)=>w.local.push({name,panel,fn}),UnregisterEventHandler:(name,panel,fn)=>{w.local=w.local.filter(x=>x.name!==name||x.panel!==panel||x.fn!==fn);}};
  vm.runInNewContext(source,{$:dollar,Date:{now:()=>w.now},Math,JSON,encodeURIComponent,String,Game:{GetMatchID:()=>w.match},Players:{GetLocalPlayer:()=>0,GetTeam:()=>2}});
 };
 w.step=(n=1,ms=200)=>{for(let i=0;i<n;i++){w.now+=ms;const due=[...w.tasks].filter(([,t])=>t.at<=w.now);for(const [id,t] of due){if(!w.tasks.has(id))continue;w.tasks.delete(id);t.fn();}}};
 w.event=(name,panel,arg=panel)=>{for(const h of [...w.local])if(h.name===name&&h.panel===panel)h.fn(arg);};
 w.destroy=c=>{w.event('PanelDeleted',c);c.alive=false;w.local=w.local.filter(h=>h.panel.IsValid());};
 w.chat=w.createChat();w.boot(w.chat);w.step(6);return w;
}
function check(name,fn){fn();passed++;console.log('PASS '+name);}
check('destroyed owner releases both global callbacks and stops navigation',()=>{const w=world();assert.equal(w.global.size,2);const n=w.urls.length;w.destroy(w.chat);w.step(10);assert.equal(w.global.size,0);assert.equal(w.urls.length,n);assert.equal(w.tasks.size,0);assert.equal(w.invalidReads,0);});
check('child deletion cannot dispose the whole chat adapter',()=>{const w=world();w.event('PanelDeleted',w.chat,w.chat.FindChildTraverse('ChatMessages'));w.step(2);assert.equal(w.global.size,2);assert.equal(w.root.__dltOwner,w.chat);});
check('late global event cleans an already invalid context without panel access',()=>{const w=world();const handlers=[...w.global.values()];w.chat.alive=false;for(const h of handlers)h.fn('DLT:ready');w.step(2);assert.equal(w.global.size,0);assert.equal(w.invalidReads,0);assert.equal(w.tasks.size,0);});
check('replacement chat waits for old owner then acquires the bridge',()=>{const w=world();const next=w.createChat();w.boot(next);w.step(3);assert.equal(w.global.size,2);assert.equal(w.root.__dltOwner,w.chat);w.destroy(w.chat);w.step(7);assert.equal(w.global.size,2);assert.equal(w.root.__dltOwner,next);assert(w.urls.some(x=>x.panel===next.FindChildTraverse('DltBridge')));});
check('100 scene owners do not accumulate global handlers or scheduled loops',()=>{const w=world();for(let i=0;i<100;i++){const next=w.createChat();w.boot(next);w.destroy(w.chat);w.chat=next;w.step(7);assert.equal(w.global.size,2);assert(w.tasks.size<=1);}w.destroy(w.chat);w.step(2);assert.equal(w.global.size,0);assert.equal(w.tasks.size,0);assert.equal(w.invalidReads,0);});
check('lobby translation preserves native parents and child order',()=>{const w=world(),P=w.Panel,lobby=new P('Panel','ChatLinesPanel',w.root),line=new P('Panel','',lobby,'ChatLineContainer'),prefix=new P('Label','',line,'ChatLinePrefix'),name=new P('Label','',line,'ChatPersona'),message=new P('Label','',line,'ChatLine');prefix.text='[TEAM]';name.text='player';message.text='wait for my ult';w.step(20);assert(line.FindChildTraverse('DltTranslation'));assert.deepEqual(line.children.slice(0,3),[prefix,name,message]);assert([prefix,name,message].every(p=>p.parent===line));assert(line.cls.has('DltLobbyTranslationRow'));});
check('match end (positive ID to zero) resets session without reloading HTML',()=>{const w=world();const old=w.packets.at(-1).session,loads=w.urls.filter(x=>!x.url.includes('#')&&!x.url.includes('?')).length;w.match=0;w.step(8);assert.notEqual(w.packets.at(-1).session,old);assert.equal(w.urls.filter(x=>!x.url.includes('#')&&!x.url.includes('?')).length,loads);assert(w.packets.some(p=>p.logs.some(e=>e.code==='session-reset')));});
check('new adapter acknowledges a pre-existing keypress instead of replaying it',()=>{const w=world();w.hotkey=19;w.destroy(w.chat);w.chat=w.createChat();w.boot(w.chat);w.step(6);w.chat.FindChildTraverse('ChatInput').focused=true;w.chat.cls.add('ChatExpanded');w.step(4);assert.equal(w.packets.at(-1).hotkeySeen,19);w.match=0;w.step(8);assert.equal(w.packets.at(-1).hotkeySeen,19);assert(!w.packets.some(p=>p.outgoing.length));});
check('navigation compatibility has a hard 800 ms cap even while chat is open',()=>{const w=world();w.transport='navigation';const input=w.chat.FindChildTraverse('ChatInput');input.focused=true;w.chat.cls.add('ChatExpanded');w.step(100,50);const nav=w.urls.filter(x=>x.url.includes('?d='));assert(nav.length>=2);for(let i=1;i<nav.length;i++)assert(nav[i].at-nav[i-1].at>=800);});
fs.writeFileSync(path.join(__dirname,'../artifacts/scene-lifecycle-tests.json'),JSON.stringify({passed,realGame:false,realGpuCrashReproduced:false},null,2));
