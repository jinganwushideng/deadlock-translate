const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path');
const defaultSource=fs.readFileSync(process.env.DLT_CHAT_TEST_SOURCE||path.join(__dirname,'../../mod/panorama/scripts/dlt_chat.js'),'utf8');
module.exports=function world(source=defaultSource){
 const w={now:100000,packets:[],jobs:new Map(),requests:[],visits:0,created:0,config:{enabled:true,fontSize:17,scanIntervalMs:200,transport:'fragment',incomingScope:'all_players',incomingTargetLanguage:'zh-Hans'},ready:()=>0,server:'fixture-server',match:1};let tick;
 class Panel{
  constructor(id,parent,cls=''){Object.assign(this,{id,parent,children:[],text:'',style:{},visible:true,alive:true,cls:new Set(cls.split(' '))});if(parent)parent.children.push(this);}
  IsValid(){return this.alive;}GetParent(){return this.parent;}GetChildCount(){w.visits++;return this.children.length;}GetChild(i){w.visits++;return this.children[i];}BHasClass(c){return this.cls.has(c);}AddClass(c){this.cls.add(c);}BHasKeyFocus(){return !!this.focused;}DeleteAsync(){w.destroy(this);}
  FindChildTraverse(id){w.visits++;if(this.id===id)return this;for(const c of this.children){const p=c.FindChildTraverse(id);if(p)return p;}return null;}
  FindChildrenWithClassTraverse(cls){w.visits++;return this.children.flatMap(c=>[...(c.cls.has(cls)?[c]:[]),...c.FindChildrenWithClassTraverse(cls)]);}
  SetURL(url){if(!url.includes('#')&&!url.includes('?')){this.title='DLT:ready';return;}w.lastUrl=url;const p=JSON.parse(decodeURIComponent(url.split('#')[1]||url.split('?d=')[1]));w.packets.push(p);if(w.holdBridge)return;
   for(const m of p.messages)if(!w.jobs.has(m.id)){w.jobs.set(m.id,{...m,readyAt:w.ready(m)});w.requests.push(m);}
   const results=[...new Set([...p.messages.map(m=>m.id),...p.poll])].flatMap(id=>{const j=w.jobs.get(id);if(!j)return [];const done=j.contextOnly||j.readyAt<=w.now;return [{id,text:done&&!j.contextOnly?'译文 '+j.text:'',revision:done&&!j.contextOnly?1:0,stage:j.contextOnly?'skipped':done?'quality':'pending',done}];});
   this.title='DLT:'+JSON.stringify({seq:p.seq,session:p.session,serverId:w.server,displayPreview:w.previewCommand,results:w.rewriteResults?w.rewriteResults(results):results,logAck:p.logs.at(-1)?.seq||0,capabilities:['diagnostic-logs','multilingual-targets'],config:{...w.config},configuredLlm:true});
  }
 }
 require('./native-bubble.cjs').install(Panel);
 w.stallNative=false;
 w.mountNative=host=>{const native=require('./native-bubble.cjs');return (w.stallNative?native.mountStalled:native.mount)((id,parent,cls)=>new Panel(id,parent,cls),host);};
 w.Panel=Panel;w.root=new Panel('Root');w.ctx=new Panel('Chat',w.root);w.history=new Panel('ChatMessages',w.ctx);w.bridge=new Panel('DltBridge',w.ctx);w.input=new Panel('ChatInput',w.ctx);new Panel('ChatTargetLabel',w.ctx);new Panel('DltMenuMount',new Panel('SubOptions',w.root));w.chatContainer=new Panel('',w.root,'ChatContainer');w.hud=new Panel('Team1Chat',w.chatContainer);w.messages=new Panel('Messages',w.hud);
 w.row=(text,parent=w.history)=>{const row=new Panel('',parent,'ChatMessage'),body=new Panel('',row,'MessageBody'),src=new Panel('MessageSource',body);new Panel('',src,'ChannelName').text='[ALLIES]';new Panel('',src,'SenderName').text='peer';new Panel('',new Panel('MessageContents',body)).text=text;return row;};
 w.bubble=(text,parent=w.messages)=>{const row=new Panel('',parent,'ChatMessage'),contents=new Panel('MessageContents',row),shape=new Panel('',contents,'ChatBubble'),container=new Panel('',shape,'TextContainer'),wrap=new Panel('',container,'MessageWrapper');new Panel('MessageText',wrap).text=text;w.mountNative(contents);return row;};
 w.destroy=p=>{p.alive=false;for(const c of [...p.children])w.destroy(c);if(p.parent)p.parent.children=p.parent.children.filter(c=>c!==p);};
 w.step=(n=1,ms=200)=>{for(let i=0;i<n;i++){w.now+=ms;tick();}};w.refresh=()=>{sandbox.DLTOpenSettings();w.step(3);};w.open=()=>{w.ctx.AddClass('ChatExpanded');w.input.focused=true;};
 const sandbox={Date:{now:()=>w.now},Math,JSON,encodeURIComponent,String,Game:{GetMatchID:()=>w.match},Players:{GetLocalPlayer:()=>0,GetTeam:()=>2},$:{GetContextPanel:()=>w.ctx,Schedule:(_,fn)=>{tick=fn;return 1;},CreatePanel:(_,parent,id)=>{w.created++;const panel=new Panel(id,parent);return id==='DltNativeText'?require('./native-bubble.cjs').prepare(panel):panel;}}};vm.runInNewContext(source,sandbox);w.step(6);return w;
};
