// Native SetURL is deliberately asynchronous: old properties/GetTitle/events may outlive navigation.
const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),fixture=path.join(__dirname,'game-window.test.cjs'),mod=new Module(fixture,module);
mod.filename=fixture;mod.paths=Module._nodeModulePaths(__dirname);mod._compile(fs.readFileSync(fixture,'utf8').split('function check(name,fn)')[0]+'\nmodule.exports=world;',fixture);
const checks=[];function check(name,fn){fn();checks.push(name);console.log('PASS '+name);}
function world(){const w=mod.exports();w.now=100000;w.sandbox.Date={now:()=>w.now};w.recovery=[];w.owner.__dltAdapterControl={resume:force=>{w.recovery.push(force);return true;}};w.tick();return w;}
function title(w,value){w.page.title=value;w.handlers.HTMLTitle(w.page,value);w.tick();}
function ready(w,load=1,seq=1){title(w,'DLTSETTINGS:ready:'+seq+':background:static:save:clean:load:'+load);}
function repeat(w,count=100){for(let i=0;i<count;i++){w.now+=200;w.tick();}}
check('one retained untagged reload command produces one navigation across 100 polls',()=>{
 const w=world();title(w,'DLTSETTINGS:ready:1');title(w,'DLTSETTINGS:reload:2');repeat(w);
 assert.equal(w.urls.length,2);assert.equal(w.queue.length,1);assert(w.visible);
});
check('tagged reload cannot be replayed by property, GetTitle or late title events',()=>{
 const w=world();ready(w);const old='DLTSETTINGS:reload:3:background:static:save:clean:load:1';title(w,old);w.page.GetTitle=()=>old;w.page.GetAttributeString=()=>old;
 for(let i=0;i<80;i++){w.handlers.HTMLTitle(w.page,old);w.now+=200;w.tick();}
 assert.equal(w.urls.length,2);assert(w.urls[1].includes('windowLoad=2'));assert(w.status.text.includes('尚未收到页面状态'));
});
check('rapid native button activation is bounded while initial and subsequent navigation is pending',()=>{
 const w=world();for(let i=0;i<50;i++)w.sandbox.DLTSettingsRetry();assert.equal(w.urls.length,1);
 ready(w);w.sandbox.DLTSettingsRetry();for(let i=0;i<50;i++)w.sandbox.DLTSettingsRetry();assert.equal(w.urls.length,2);
});
check('manual retry is available after a loading deadline without automatic retry storms',()=>{
 const w=world();repeat(w,60);assert.equal(w.urls.length,1);assert(w.help.shown);assert.equal(w.sandbox.DLTSettingsRetry(),true);assert.equal(w.urls.length,2);
 for(let i=0;i<30;i++)w.sandbox.DLTSettingsRetry();assert.equal(w.urls.length,2);
});
check('a new document can legitimately reload once after each completed navigation',()=>{
 const w=world();ready(w);title(w,'DLTSETTINGS:reload:2:background:static:save:clean:load:1');assert.equal(w.urls.length,2);
 ready(w,2);title(w,'DLTSETTINGS:reload:2:background:static:save:clean:load:2');assert.equal(w.urls.length,3);
 ready(w,3);repeat(w,20);assert.equal(w.urls.length,3);assert(w.status.text.includes('设置页已连接'));
});
check('old ready, reload, close and editor commands cannot affect the current document',()=>{
 const w=world();w.host.SetHasClass=()=>{};ready(w);title(w,'DLTSETTINGS:reload:2:background:static:save:clean:load:1');ready(w,2);
 for(const state of ['ready:100','reload:101','close','edit:'+'c'.repeat(48)+':102:0:'+Array(16).fill(10).join(',')])title(w,'DLTSETTINGS:'+state+':background:blur:save:dirty:load:1');
 assert.equal(w.urls.length,2);assert(w.visible);w.sandbox.DLTSettingsClose('escape');assert(!w.visible,'stale dirty metadata cannot block a clean current page');
});
check('stale untagged ready properties cannot declare a pending navigation complete',()=>{
 const w=world();title(w,'DLTSETTINGS:ready:7');w.sandbox.DLTSettingsRetry();repeat(w,10);w.sandbox.DLTSettingsRetry();assert.equal(w.urls.length,2);
 title(w,'DLTSETTINGS:ready:8');assert(w.status.text.includes('设置页已连接'));w.sandbox.DLTSettingsRetry();assert.equal(w.urls.length,3);
});
check('settings reload does not force-reset the healthy translation bridge',()=>{
 const w=world();ready(w);title(w,'DLTSETTINGS:reload:2:background:static:save:clean:load:1');ready(w,2);assert(w.recovery.every(force=>force===false));
});
check('explicit restore-connection remains a separate one-shot forced recovery',()=>{
 const w=world();ready(w);title(w,'DLTSETTINGS:reconnect:2:background:static:save:clean:load:1');repeat(w,20);
 assert.equal(w.recovery.filter(Boolean).length,1);assert.equal(w.urls.length,1);
});
check('native reload refuses unsaved configuration and keeps the page intact',()=>{
 const w=world();ready(w);title(w,'DLTSETTINGS:ready:2:background:static:save:error:load:1');assert.equal(w.sandbox.DLTSettingsRetry(),false);assert.equal(w.urls.length,1);assert(w.helpText.text.includes('未保存修改'));
});
check('navigation failure allows a later explicit retry without another polling loop',()=>{
 const w=world();ready(w);let failed=0,normal=w.page.SetURL;w.page.SetURL=()=>{failed++;throw Error('fixture SetURL failed');};w.sandbox.DLTSettingsRetry();assert(w.help.shown);w.page.SetURL=normal;w.sandbox.DLTSettingsRetry();assert.equal(failed,1);assert.equal(w.urls.length,2);repeat(w,10);assert.equal(w.queue.length,1);
});
check('closing during an unfinished load cancels its poll and rejects late replies',()=>{
 const w=world();ready(w);title(w,'DLTSETTINGS:reload:2:background:static:save:clean:load:1');w.sandbox.DLTSettingsClose('escape');repeat(w,10);assert(!w.visible);assert.equal(w.queue.length,0);assert.equal(w.urls.length,2);
});
const task=path.join(root,'artifacts/reload-frame-disconnect-20261027');fs.mkdirSync(task,{recursive:true});fs.writeFileSync(path.join(task,'native-reload-tests.json'),JSON.stringify({passed:checks.length,checks,realGame:false,scope:'real script in asynchronous title/navigation mock, not CEF crash reproduction'},null,2));
console.log(JSON.stringify({passed:checks.length}));
