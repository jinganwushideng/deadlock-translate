// Execute production adapter under the observed native contract, with no private editor.
const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const fixture=path.join(__dirname,'outgoing-ui.test.cjs'),loaded=new Module(fixture,module);
loaded.filename=fixture;loaded.paths=Module._nodeModulePaths(__dirname);
let fixtureCode=fs.readFileSync(fixture,'utf8').split('const channelOfTarget =')[0];
if(process.env.DLT_SEND_SOURCE)fixtureCode=fixtureCode.replace(/const CHAT_SCRIPT = [^\n]+;/,'const CHAT_SCRIPT = fs.readFileSync('+JSON.stringify(process.env.DLT_SEND_SOURCE)+',"utf8");');
loaded._compile(fixtureCode+'\nmodule.exports=world;',fixture);
const world=loaded.exports,checks=[];
function test(name,run){try{run();checks.push({name,ok:true});}catch(error){checks.push({name,ok:false,error:String(error.stack||error)});}}
function setup(opts={}){const w=world({nativeClasses:true,diagnostics:true,statusCapability:true,keepSubmittedText:true,undefinedDispatch:true,...opts});w.step(60);return w;}
const events=w=>w.packets.flatMap(p=>p.logs||[]),statuses=w=>w.packets.flatMap(p=>p.outgoingStatus||[]);
const part=(w,index,text,total=2,id='out-1')=>({...w.resultFor(w.jobs[id].item),partIndex:index,partCount:total,sendText:text,state:'ready'});
for(const channel of ['team','all','party'])test(channel+': first shortcut closes across callbacks then sends without further input',()=>{
 const w=setup({deferResult:false});w.openChat(channel);w.shortcut('原生第一条');w.step(2);assert.equal(w.submitted.length,0);assert(w.jobs['out-1'].holding);assert.equal(w.jobs['out-1'].item.text,'原生第一条');assert(!statuses(w).some(s=>s.state==='capture_ready'));
 w.step(18);assert.equal(w.submitted.length,1);assert.equal(w.submitted[0].channel,{team:'ChatTarget_GameAllies',all:'ChatTarget_GameAll',party:'ChatTarget_Party'}[channel]);
 assert(events(w).some(e=>e.code==='send-close-confirmed'));const close=w.calls.find(c=>c.name==='CitadelChatInputSubmitted');assert(w.submitted[0].at-close.at>=350);assert.equal(w.draft(),null);
});
test('ignored first close recovers without a second shortcut',()=>{
 const w=setup({stubbornClose:2,deferResult:false});w.openChat('team');w.capture('first');w.step(20);assert.equal(w.closeIgnored,2);assert.equal(w.submitted.length,1);assert(events(w).some(e=>e.code==='send-close-retry'));
});
test('permanent close failure retains original in a failed task and never replays',()=>{
 const w=setup({stubbornClose:99,deferResult:false});w.openChat('team');w.capture('保留原文');w.step(40);assert.equal(w.closeIgnored,3);assert.equal(w.submitted.length,0);assert.equal(w.jobs['out-1'].item.text,'保留原文');assert.equal(w.jobs['out-1'].state,'failed');
 assert(w.packets.some(p=>(p.outgoingRefused||[]).some(r=>r.reason==='native-close')));w.closeChat();w.step(30);assert.equal(w.submitted.length,0);
});
test('close cancellation preserves a new native draft and later resumes the captured original',()=>{
 const w=setup({stubbornClose:99});w.openChat('team');w.shortcut('captured first');w.step(2);w.openChat('team');w.input.text='第二条';w.input.selection=[1,2];w.input.caret=2;w.input.composition='zhong';
 const calls=w.calls.length;w.deferResult=false;w.step(20);assert.equal(w.input.text,'第二条');assert.deepEqual(w.input.selection,[1,2]);assert.equal(w.input.composition,'zhong');assert.equal(w.submitted.length,0);assert(!w.calls.slice(calls).some(c=>/Submitted|Blur/.test(c.name)));
 w.closeChat();w.step(20);assert.equal(w.jobs['out-1'].item.text,'captured first');assert.equal(w.submitted.length,1);assert.equal(w.input.text,'第二条');
});
test('second native edit reports waiting_input and preserves text caret selection and IME',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.openChat('team');w.input.text='第二条草稿';w.input.selection=[0,2];w.input.caret=2;w.input.composition='pinyin';w.deferResult=false;const before=w.calls.length;w.step(20);
 assert.equal(w.submitted.length,0);assert.equal(w.input.text,'第二条草稿');assert(w.input.focused);assert.deepEqual(w.input.selection,[0,2]);assert.equal(w.input.caret,2);assert.equal(w.input.composition,'pinyin');assert(statuses(w).some(s=>s.state==='waiting_input'&&s.reason==='input'));assert(!statuses(w).some(s=>s.state==='waiting_channel'));assert(!w.calls.slice(before).some(c=>/Submitted|Blur/.test(c.name)));
 w.closeChat();w.step(20);assert.equal(w.submitted.length,1);assert.equal(w.input.text,'第二条草稿');
});
test('an empty reopened native editor waits for the player to close it',()=>{
 const w=setup();w.openChat('all');w.capture('first');w.openChat('all');w.deferResult=false;w.step(15);assert.equal(w.submitted.length,0);assert(w.input.focused);w.closeChat();w.step(15);assert.equal(w.submitted.length,1);
});
test('focus alone blocks when presentation classes temporarily disappear',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.openChat('team');w.context.classes.delete('ChatExpanded');w.input.text='still typing';w.deferResult=false;w.step(20);assert.equal(w.submitted.length,0);assert.equal(w.input.text,'still typing');w.closeChat();w.step(20);assert.equal(w.submitted.length,1);
});
test('a different channel draft is untouched and closing it waits for the original channel',()=>{
 const w=setup();w.openChat('team');w.capture('private');w.openChat('all');w.input.text='public draft';w.deferResult=false;w.step(15);assert.equal(w.submitted.length,0);assert.equal(w.input.text,'public draft');w.closeChat();w.step(15);assert.equal(w.submitted.length,0);assert(statuses(w).some(s=>s.state==='waiting_channel'));
 w.openChat('team');w.closeChat();w.step(20);assert.equal(w.submitted.length,1);assert.equal(w.submitted[0].channel,'ChatTarget_GameAllies');
});
test('ordinary Enter stays native and creates no manual queue',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.openChat('team');w.input.text='Plain "text";';w.enterNative();assert.equal(w.submitted[0].text,'Plain "text";');w.deferResult=false;w.step(20);assert.equal(w.submitted.length,2);assert.equal(Object.keys(w.jobs).length,1);assert(!w.packets.some(p=>(p.outgoing||[]).some(x=>x.id.startsWith('manual-'))));
});
test('two captured shortcuts retain channel order and global 900ms spacing',()=>{
 const w=setup({spec:{'out-1':{sendText:'first translation'},'out-2':{sendText:'second translation'}}});w.openChat('team');w.capture('first');w.openChat('team');w.capture('second');w.deferResult=false;w.step(40);assert.deepEqual(w.submitted.map(s=>s.text),['first translation','second translation']);assert(w.submitted[1].at-w.submitted[0].at>=900);
});
test('bilingual separated segments finish despite retained submit text and missing echo',()=>{
 const w=setup({displayMode:'bilingual',config:{outgoingSeparateBilingual:true,outgoingSplitLong:true}});w.openChat('team');w.capture('原文');assert.equal(w.jobs['out-1'].item.separateBilingual,true);w.pushResults([part(w,0,'原文')]);w.step(10);assert.equal(w.submitted.length,1);w.step(85);assert(statuses(w).some(s=>s.part===0&&s.state==='echo_unobserved'));
 w.pushResults([part(w,1,'translation')]);w.step(20);assert.deepEqual(w.submitted.map(s=>s.text),['原文','translation']);assert(statuses(w).some(s=>s.part===1&&s.state==='dispatched'));
});
test('long multipart parts are paced and repeated results never replay',()=>{
 const w=setup({config:{outgoingSplitLong:true}});w.openChat('party');w.capture('long');for(let i=0;i<4;i++){w.pushResults([part(w,i,String(i).repeat(80),4)]);w.step(15);w.pushResults([part(w,i,String(i).repeat(80),4)]);w.step(10);}
 assert.equal(w.submitted.length,4);assert(w.submitted.every(x=>x.text.length===80&&x.channel==='ChatTarget_Party'));for(let i=1;i<4;i++)assert(w.submitted[i].at-w.submitted[i-1].at>=900);assert.deepEqual([...new Set(w.packets.flatMap(p=>p.outgoingPartAcks||[]).map(a=>a.part))],[0,1,2,3]);
});
test('distinct segment local self echoes are observed independently with no body in status or logs',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.pushResults([part(w,0,'source')]);w.step(10);w.pushResults([part(w,1,'translation')]);w.step(15);w.addEcho('chat','team',true,'source');w.addEcho('chat','team',true,'translation');w.step(12);for(let part=0;part<2;part++)assert(statuses(w).some(s=>s.part===part&&s.state==='echo_observed'));assert(!JSON.stringify(statuses(w)).includes('translation'));assert(!JSON.stringify(events(w)).includes('source'));
});
test('identical text in different tasks dispatches twice but one echo cannot identify a task',()=>{
 const w=setup();w.openChat('team');w.capture('same first');w.openChat('team');w.capture('same second');w.deferResult=false;w.step(30);assert.equal(w.submitted.length,2);w.addEcho('chat','team',true,'Wait for my ult');w.step(10);assert(!statuses(w).some(s=>s.state==='echo_observed'));assert.equal(new Set(statuses(w).filter(s=>s.state==='echo_unobserved'&&s.reason==='ambiguous').map(s=>s.id)).size,2);
});
test('definite wrong-channel self echo pauses the remaining segment',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.pushResults([part(w,0,'private source')]);w.step(10);w.addEcho('chat','all',true,'private source');w.step(8);w.pushResults([part(w,1,'translation')]);w.step(20);assert.equal(w.submitted.length,1);assert(statuses(w).some(s=>s.state==='waiting_channel'&&s.reason==='channel-fault'));
});
test('peer rows cannot confirm local echo and missing observation never resends',()=>{
 const w=setup({deferResult:false});w.openChat('team');w.capture('first');w.step(12);w.addEcho('chat','team',false,'Wait for my ult');w.step(10);assert(!statuses(w).some(s=>s.state==='echo_observed'));w.step(85);assert(statuses(w).some(s=>s.state==='echo_unobserved'));assert.equal(w.submitted.length,1);
});
test('a lost release response retries its metadata without dropping the retained task or replaying',()=>{
 const w=setup({deferResult:false});w.dropReleaseOnce=true;w.openChat('team');w.capture('retained retry');w.step(100);assert.equal(w.submitted.length,1);const releases=statuses(w).filter(s=>s.state==='capture_ready');assert(releases.length>=2);assert.equal(new Set(releases.map(s=>s.seq)).size,1);
});
test('old backend capability omits optional status while retaining normal dispatch',()=>{
 const w=setup({statusCapability:false,deferResult:false});w.openChat('all');w.capture('first');w.step(105);assert.equal(w.submitted.length,1);assert(w.packets.every(p=>p.outgoingStatus===undefined));
});
test('settings focus blocks capture closing retries and submits until settings closes',()=>{
 const w=setup();w.openChat('team');w.capture('first');w.setSettingsOpen(true);const before=w.calls.length;w.deferResult=false;w.openChat('team');w.input.text='native draft';w.trigger++;w.step(20);assert.equal(w.submitted.length,0);assert.equal(w.input.text,'native draft');assert.equal(Object.keys(w.jobs).length,1);assert(statuses(w).some(s=>s.state==='waiting_input'&&s.reason==='settings'));assert(!w.calls.slice(before).some(c=>/Submitted|Blur/.test(c.name)));w.setSettingsOpen(false);w.closeChat();w.step(20);assert.equal(w.submitted.length,1);
});
test('waiting input expires with content retained and no send after close',()=>{
 const w=setup({timeout:1});w.openChat('team');w.capture('retained');w.openChat('team');w.input.text='new draft';w.deferResult=false;w.step(80);assert.equal(w.submitted.length,0);assert.equal(w.input.text,'new draft');assert.equal(w.jobs['out-1'].item.text,'retained');assert(w.packets.some(p=>(p.outgoingRefused||[]).some(r=>r.reason==='timeout')));w.closeChat();w.step(20);assert.equal(w.submitted.length,0);
});
for(const kind of ['backend','match'])test(kind+' reset removes old tasks without consuming new draft or replaying',()=>{
 const w=setup();w.openChat('team');w.capture('old task');w.openChat('team');w.input.text='new draft';if(kind==='backend'){w.serverId='new-backend';w.trigger=0;w.jobs={};}else w.matchId='2';w.deferResult=false;w.step(25);assert.equal(w.submitted.length,0);assert.equal(w.input.text,'new draft');w.closeChat();w.step(20);assert.equal(w.submitted.length,0);
});
test('rejected submit is refused; unknown accepted dispatch is never retried',()=>{
 const rejected=setup({rejectSubmit:true,deferResult:false});rejected.openChat('all');rejected.capture('refused');rejected.step(30);assert.equal(rejected.submitted.length,0);assert(rejected.packets.some(p=>(p.outgoingRefused||[]).some(r=>r.reason==='submit-rejected')));
 const unknown=setup({ignoreSubmitOnce:true,deferResult:false});unknown.openChat('all');unknown.capture('unknown');unknown.step(110);assert.equal(unknown.submitted.length,0);assert.equal(statuses(unknown).filter(s=>s.state==='dispatched').length,1);assert(statuses(unknown).some(s=>s.state==='echo_unobserved'));
});
const report={realGame:false,cloudCalls:0,contract:'native editor; retained submitted text; undefined dispatch return; no private draft',retired:'private draft and manual queue assertions replaced with native editor waiting tests',passed:checks.filter(c=>c.ok).length,total:checks.length,checks};
const out=path.join(__dirname,'../artifacts/send-native-20261027');fs.mkdirSync(out,{recursive:true});fs.writeFileSync(path.join(out,'native-send-tests.json'),JSON.stringify(report,null,2));for(const c of checks)if(!c.ok)console.error('FAIL: '+c.name+'\n'+c.error);console.log(JSON.stringify({passed:report.passed,total:report.total,realGame:false}));if(report.passed!==report.total)process.exitCode=1;
