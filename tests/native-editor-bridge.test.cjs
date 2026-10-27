const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),world=require('./helpers/bridge-world.cjs');
let passed=0;function check(name,fn){fn();passed++;console.log('PASS '+name);}
const id='e'.repeat(48);
function active(){const w=world();w.root.__dltActiveSettingsWindow=new w.Panel('Panel','DltSettingsWindowHost',w.root);w.nativeEditorExchange=c=>({id:c.id,request:c.request,state:c.action==='claim'?'editing':c.action==='apply'?'applied':'cancelled',...c.action==='claim'?{label:'测试文本',value:'原始中文',multiline:true,maxLength:600}:{}});return w;}
check('native editing reuses the healthy main HTML without creating another surface',()=>{
 const w=active(),bridge=w.chat.FindChildTraverse('DltBridge'),loads=w.urls.filter(x=>!x.url.includes('#')&&!x.url.includes('?')).length;
 w.root.__dltNativeEditorCommand={id,request:1,action:'claim'};w.step(8);
 assert.equal(w.root.__dltNativeEditorReply.state,'editing');assert.equal(w.root.__dltNativeEditorReply.value,'原始中文');assert.equal(w.root.__dltNativeEditorCommand,null);
 assert(w.urls.every(x=>x.panel===bridge));assert.equal(w.urls.filter(x=>!x.url.includes('#')&&!x.url.includes('?')).length,loads);
});
check('Chinese editor text stays outside incoming/outgoing translation and diagnostic payloads',()=>{
 const w=active(),value='只属于设置草稿的中文'.repeat(40);w.root.__dltNativeEditorCommand={id,request:2,action:'apply',value};w.step(8);
 const packet=w.packets.find(p=>p.nativeEditor);assert.equal(packet.nativeEditor.value,value);assert.deepEqual(packet.messages,[]);assert.deepEqual(packet.outgoing,[]);assert.deepEqual(packet.logs,[]);
 assert(!JSON.stringify(packet.poll).includes(value));assert.equal(w.root.__dltNativeEditorReply.state,'applied');
});
check('closed or closing settings never send an orphaned editor draft',()=>{
 const w=active();w.root.__dltActiveSettingsWindow.__dltClosing=true;w.root.__dltNativeEditorCommand={id,request:3,action:'apply',value:'未提交的草稿'};w.step(12);
 assert(!w.packets.some(p=>p.nativeEditor));assert.equal(w.root.__dltNativeEditorCommand,null);assert.equal(w.root.__dltNativeEditorReply,null);
});
check('late previous replies cannot replace the current editor command',()=>{
 const w=active();w.silent=true;w.root.__dltNativeEditorCommand={id,request:1,action:'claim'};w.step(1);const old=w.packets.at(-1);
 // Silent fixtures retain the request URL but do not parse it, so recover the last envelope.
 const envelope=old?.nativeEditor?old:JSON.parse(decodeURIComponent(w.urls.at(-1).url.split('#')[1]));
 w.root.__dltNativeEditorCommand={id,request:2,action:'claim'};w.root.__dltNativeEditorReply=null;
 w.chat.FindChildTraverse('DltBridge').title='DLT:'+JSON.stringify({seq:envelope.seq,session:envelope.session,serverId:'fixture-server',results:[],outgoing:[],logAck:0,nativeEditor:{id,request:1,state:'editing',value:'过时草稿'},config:{enabled:true,incomingScope:'all_players',transport:'fragment',scanIntervalMs:200,outgoingEnabled:true,fontSize:17},capabilities:['native-settings-editor','diagnostic-logs']});
 w.silent=false;w.step(1);assert.equal(w.root.__dltNativeEditorReply,null);assert.equal(w.root.__dltNativeEditorCommand.request,2);
 w.step(8);assert.equal(w.root.__dltNativeEditorReply.request,2);assert.equal(w.root.__dltNativeEditorReply.value,'原始中文');
});
check('an unsupported older backend gives a bounded editor error while heartbeats continue',()=>{
 const w=active();delete w.nativeEditorExchange;w.root.__dltNativeEditorCommand={id,request:4,action:'claim'};w.step(12);
 assert.equal(w.root.__dltNativeEditorReply.state,'error');assert(w.root.__dltNativeEditorReply.message.includes('成套更新'));const before=w.packets.length;w.step(20);assert(w.packets.length>before);
});
check('oversized encoded drafts remain editable instead of breaking the game bridge',()=>{
 const w=active();w.root.__dltNativeEditorCommand={id,request:5,action:'apply',value:'中'.repeat(4096)};w.step(12);
 assert.equal(w.root.__dltNativeEditorReply.state,'error');assert(w.root.__dltNativeEditorReply.message.includes('通信长度'));assert(!w.packets.some(p=>p.nativeEditor));
 const before=w.packets.length;w.step(20);assert(w.packets.length>before);
});
fs.writeFileSync(path.join(__dirname,'../artifacts/native-editor-bridge-tests.json'),JSON.stringify({passed,realGame:false,cloudCalls:0},null,2));console.log('TOTAL '+passed);
