const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const rootPath=path.resolve(__dirname,'..');
const menuSource=fs.readFileSync(process.env.DLT_MENU_TEST_SCRIPT||path.join(rootPath,'mod/panorama/scripts/dlt_menu.js'),'utf8');
const chatSource=fs.readFileSync(process.env.DLT_CHAT_TEST_SOURCE||path.join(rootPath,'mod/panorama/scripts/dlt_chat.js'),'utf8');
let passed=0;
function check(name,fn){fn();passed++;console.log('PASS '+name);}
class Panel {
 constructor(type,id,parent,classes=''){this.type=type;this.id=id;this.parent=parent;this.classes=new Set(classes.split(' '));this.children=[];this.style={};this.visible=true;this.text='';this.alive=true;if(parent)parent.children.push(this);}
 IsValid(){return this.alive;}GetParent(){return this.parent;}GetChildCount(){return this.children.length;}GetChild(i){return this.children[i];}
 BHasClass(name){return this.classes.has(name);}AddClass(name){this.classes.add(name);}
 FindChildTraverse(id){if(this.id===id)return this;for(const child of this.children){const result=child.FindChildTraverse(id);if(result)return result;}return null;}
 FindChildrenWithClassTraverse(name){return this.children.flatMap(c=>[...(c.BHasClass(name)?[c]:[]),...c.FindChildrenWithClassTraverse(name)]);}
 MoveChildBefore(child,before){this.children=this.children.filter(c=>c!==child);this.children.splice(this.children.indexOf(before),0,child);}
 BLoadLayout(url){this.loaded=url;return true;}SetURL(){this.title='DLT:ready';}DeleteAsync(){this.alive=false;}
}
function fixture(qol=true,nativeRow=true){
 const root=new Panel('Root','Root'),owner=new Panel('CitadelHudEscapeMenu','Escape',root);
 new Panel('Panel','EscapeBackground',owner);const menu=new Panel('Panel','Menu',owner),options=new Panel('Panel','SubOptions',menu);
 options.actualuiscale_y=1;options.actuallayoutheight=228;
 const primary=[];for(const [id,margin] of [['newgame',490],['watchgame',420],['guides',350],['changehero',280]]){
  const button=new Panel('Button',id,menu,'nav_menu_item primary');button.style.marginBottom=(qol?margin:280)+'px';
  const label=new Panel('Label',id+'Label',button,'menuButtonLabel');label.style.marginBottom='0px';primary.push(button,label);
 }
 const feedback=new Panel('Panel','feedback',options,'FeedbackRow');
 if(qol)new Panel('Button','QolSettings',feedback,'nav_menu_item minor');
 let row;if(nativeRow){row=new Panel('Panel','nativeSettingsRow',options,'SettingsRow');new Panel('Button','settings',row);new Panel('Button','QuickMute',row);}
 const quit=new Panel('Button','quit',options);const chat=new Panel('CitadelChat','Chat',root);new Panel('Panel','ChatMessages',chat);new Panel('HTML','DltBridge',chat);
 return {root,owner,menu,options,row,quit,chat,primary};
}
for(const qol of [false,true])check((qol?'QOL':'native')+' menu margins are unchanged after adapter initialization and opening settings',()=>{
 const w=fixture(qol);const mount=new Panel('Panel','DltMenuMount',w.row);new Panel('Label','DltMenuStatus',mount);
 const before=w.primary.map(p=>JSON.stringify(p.style));let queue=[],created=0;
 const sandbox={$:{GetContextPanel:()=>mount,Schedule:(_,fn)=>queue.push(fn),CreatePanel:(type,parent,id)=>{created++;return new Panel(type,id,parent);}}};
 vm.runInNewContext(menuSource,sandbox);for(const fn of queue.splice(0))fn();sandbox.DLTMenuOpenSettings();sandbox.DLTMenuOpenSettings();
 assert.deepEqual(w.primary.map(p=>JSON.stringify(p.style)),before);assert.equal(created,1);assert.equal(w.owner.__dltSettingsWindow.parent,w.owner);
 assert(w.owner.__dltSettingsWindow.loaded.endsWith('/dlt_settings_window.xml'));
});
for(const qol of [false,true])check((qol?'QOL':'native')+' receives one inline settings control without another SubOptions row',()=>{
 const w=fixture(qol);const before=w.options.children.slice();let tick,now=10000;
 const sandbox={Date:{now:()=>now},String,Math,JSON,encodeURIComponent,Players:{GetLocalPlayer:()=>0,GetTeam:()=>2},$:{GetContextPanel:()=>w.chat,Schedule:(_,fn)=>tick=fn,RegisterEventHandler:()=>{},RegisterForUnhandledEvent:()=>{},Msg:()=>{},CreatePanel:(type,parent,id)=>new Panel(type,id,parent)}};
 vm.runInNewContext(chatSource,sandbox);tick();const mount=w.options.FindChildTraverse('DltMenuMount');
 assert(mount);assert.equal(mount.parent,w.row);assert.equal(mount.style.width,'fit-children');assert(mount.BHasClass('DltMenuInlineMount'));
 assert.deepEqual(w.options.children,before);for(let i=0;i<55;i++){now+=200;tick();}assert.equal(w.row.children.filter(p=>p.id==='DltMenuMount').length,1);
 if(qol)assert(w.options.FindChildTraverse('QolSettings'));
});
check('menu variant without native settings row has one bounded fallback before quit',()=>{
 const w=fixture(true,false);let tick;
 const sandbox={Date:{now:()=>10000},String,Math,JSON,encodeURIComponent,$:{GetContextPanel:()=>w.chat,Schedule:(_,fn)=>tick=fn,RegisterEventHandler:()=>{},RegisterForUnhandledEvent:()=>{},Msg:()=>{},CreatePanel:(type,parent,id)=>new Panel(type,id,parent)}};
 vm.runInNewContext(chatSource,sandbox);tick();const mount=w.options.FindChildTraverse('DltMenuMount');assert(mount);assert.equal(mount.parent,w.options);assert(w.options.children.indexOf(mount)<w.options.children.indexOf(w.quit));
});
function bootMenu(w){
 let tick,now=10000,lookups=0;
 const search=w.root.FindChildTraverse.bind(w.root);w.root.FindChildTraverse=id=>{if(id==='SubOptions')lookups++;return search(id);};
 const sandbox={Date:{now:()=>now},String,Math,JSON,encodeURIComponent,Players:{GetLocalPlayer:()=>0,GetTeam:()=>2},$:{GetContextPanel:()=>w.chat,Schedule:(_,fn)=>tick=fn,RegisterEventHandler:()=>{},RegisterForUnhandledEvent:()=>{},Msg:()=>{},CreatePanel:(type,parent,id)=>new Panel(type,id,parent)}};
 vm.runInNewContext(chatSource,sandbox);
 return {step(n=1){for(let i=0;i<n;i++){now+=200;tick();}},get lookups(){return lookups;}};
}
check('settings mounts immediately even after thousands of unrelated UI nodes',()=>{
 const w=fixture(true),heavy=new Panel('Panel','HeavyHud',w.root);
 for(let i=0;i<6000;i++)new Panel('Panel','unrelated'+i,heavy);
 w.root.children=w.root.children.filter(p=>p!==heavy&&p!==w.owner);w.root.children.unshift(heavy);w.root.children.push(w.owner);
 const before=w.primary.map(p=>JSON.stringify(p.style)),control=bootMenu(w);control.step();
 assert(w.options.FindChildTraverse('DltMenuMount'),'menu must not wait behind incremental HUD traversal');
 assert.deepEqual(w.primary.map(p=>JSON.stringify(p.style)),before);control.step(100);
 assert.equal(control.lookups,1,'mounted menu never repeats the native root lookup');
});
check('native layout boundaries do not hide settings from the menu bootstrap',()=>{
 const w=fixture(true);w.owner.GetChildCount=()=>0;const control=bootMenu(w);control.step();
 assert(w.options.FindChildTraverse('DltMenuMount'),'native lookup must reach SubOptions across enumeration boundaries');
 assert.equal(control.lookups,1);
});
check('late menu lookup is rate limited then recovers without restarting chat',()=>{
 const w=fixture(true);w.root.children=w.root.children.filter(p=>p!==w.owner);w.owner.parent=null;
 const control=bootMenu(w);control.step(30);assert(control.lookups<=4,'missing menu must not trigger a full lookup every tick');
 w.owner.parent=w.root;w.root.children.push(w.owner);control.step(12);
 assert(w.options.FindChildTraverse('DltMenuMount'),'late menu must mount promptly');
 const stable=control.lookups;control.step(80);assert.equal(control.lookups,stable);
});
check('detached but valid old mount cannot block a rebuilt menu',()=>{
 const w=fixture(true),control=bootMenu(w);control.step();const old=w.options.FindChildTraverse('DltMenuMount');assert(old);
 const parent=old.parent;parent.children=parent.children.filter(p=>p!==old);old.parent=null;
 control.step(12);const current=w.options.FindChildTraverse('DltMenuMount');assert(current&&current!==old);
 assert.equal(parent.children.filter(p=>p.id==='DltMenuMount').length,1);
});
check('menu CSS confines size and spacing changes to translator controls',()=>{
 const css=fs.readFileSync(path.join(rootPath,'mod/panorama/styles/dlt_menu.css'),'utf8');
 assert(css.includes('#DltMenuMount.DltMenuInlineMount'));assert(css.includes('width: fit-children'));
 assert(!/#(?:Menu|SubOptions|changehero|newgame|watchgame|guides)\b/.test(css));
});
fs.writeFileSync(path.join(rootPath,'artifacts/menu-layout-tests.json'),JSON.stringify({passed,scope:'mock Panorama native/QOL menu geometry ownership and mounting; real-game layout not verified'},null,2));
