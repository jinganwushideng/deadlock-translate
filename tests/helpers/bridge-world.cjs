// Native scene lifetimes, not a screenshot/FPS or real GPU crash reproduction.
const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const source=fs.readFileSync(process.env.DLT_CHAT_TEST_SOURCE||path.join(__dirname,'../../mod/panorama/scripts/dlt_chat.js'),'utf8');let passed=0;
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
  GetTitle(){return w.titleViaMethod?this.latestTitle||'':'';}
  SetURL(url){this.guard();w.urls.push({url,at:w.now,panel:this});if(w.silent)return;if(!url.includes('#')&&!url.includes('?')){this.latestTitle='DLT:ready';this.title=w.staleProperty?'engine loading':this.latestTitle;return;}
   const data=JSON.parse(decodeURIComponent(url.split('#')[1]||url.split('?d=')[1]));w.packets.push(data);
   this.latestTitle='DLT:'+JSON.stringify({seq:data.seq,session:data.session,serverId:'fixture-server',nativeEditor:data.nativeEditor&&w.nativeEditorExchange?w.nativeEditorExchange(data.nativeEditor):null,results:data.messages.map(m=>({id:m.id,text:'译文',revision:1,done:true})),outgoing:[],hotkey:w.hotkey>data.hotkeySeen?w.hotkey:0,logAck:data.logs.at(-1)?.seq||0,capabilities:['diagnostic-logs','outgoing-party-channel'],config:{enabled:true,incomingScope:'all_players',transport:w.transport,scanIntervalMs:200,outgoingEnabled:true,fontSize:17}});this.title=w.staleProperty?'engine loading':this.latestTitle;
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
module.exports=world;
