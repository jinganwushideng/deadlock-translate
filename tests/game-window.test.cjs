const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const source=fs.readFileSync(process.env.DLT_WINDOW_TEST_SOURCE||path.join(__dirname,'../mod/panorama/scripts/dlt_settings_window.js'),'utf8');let passed=0;
function world(options={}){
 const w={queue:[],urls:[],events:[],handlers:{},unregistered:[],visible:true,escape:true,status:{text:'',IsValid:()=>true}};
 w.owner={IsValid:()=>true,visible:true,BHasClass:()=>w.escape,GetParent:()=>null};
 w.page={IsValid:()=>true,title:'',SetURL:url=>w.urls.push(url),SetFocus:()=>{w.focused=!options.requireInputContext||w.activeInputContext===w.host;return w.focused;},BAcceptsInput:()=>true,BAcceptsFocus:()=>true,BHasKeyFocus:()=>!!w.focused};
 w.help={shown:false,SetHasClass:(_,value)=>w.help.shown=value,IsValid:()=>true};w.helpTitle={text:'',IsValid:()=>true};w.helpText={text:'',IsValid:()=>true};
 w.context={IsValid:()=>w.visible,GetParent:()=>w.owner,FindChildTraverse:id=>id==='DltSettingsPage'?w.page:id==='DltConnectionHelp'?w.help:id==='DltConnectionHelpTitle'?w.helpTitle:id==='DltConnectionHelpText'?w.helpText:w.status,DeleteAsync:()=>w.visible=false};
 w.host={id:'DltSettingsWindowHost',__dltSettingsHost:options.ownership!==false,IsValid:()=>w.visible,visible:true,GetParent:()=>w.owner,BHasClass:()=>false,FindChildTraverse:w.context.FindChildTraverse,DeleteAsync:()=>w.visible=false};
 w.inputContextCalls=[];w.host.SetTopOfInputContext=enabled=>{w.inputContextCalls.push(enabled);w.activeInputContext=enabled?w.host:w.owner;};
 w.context.GetParent=()=>w.host;
 w.nativeChat={IsValid:()=>true,GetParent:()=>w.owner,FindChildTraverse:w.context.FindChildTraverse,DeleteAsync:()=>w.nativeDeleted=true};
 if(options.contextIsNative)w.owner.__dltActiveSettingsHost=w.host;
 if(options.foreignHost)w.host.GetParent=()=>({IsValid:()=>true,GetParent:()=>null});
 if(options.scene){
  const panel=id=>({id,visible:true,style:{},classes:new Set(),IsValid:()=>true,SetHasClass(name,on){on?this.classes.add(name):this.classes.delete(name);}});
  w.scene={};for(const id of ['DltSettingsRoot','DltWindowBackdrop','EscapeBackground','Menu','LeftStripe','RightSide','LeftStripeBlur','RightSideBlur','DltSurfaceProbeInfo','DltSurfaceProbeText','HTMLTexture'])w.scene[id]=panel(id);
  const styleKey=name=>name==='background-color'?'backgroundColor':name==='texture-sampling'?'textureSampling':name;
  w.page.style={};w.page.ClearPropertyFromCode=name=>{delete w.page.style[styleKey(name)];};
  w.page.classes=new Set();w.page.SetHasClass=function(name,on){on?this.classes.add(name):this.classes.delete(name);};w.page.FindChildTraverse=id=>id==='HTMLTexture'?w.scene.HTMLTexture:null;
  w.scene.HTMLTexture.cleared=[];w.scene.HTMLTexture.ClearPropertyFromCode=name=>{w.scene.HTMLTexture.cleared.push(name);delete w.scene.HTMLTexture.style[styleKey(name)];};
  w.scene.RightSide.visible=false;w.host.classes=new Set();w.host.SetHasClass=function(name,on){on?this.classes.add(name):this.classes.delete(name);};
  w.scene.DltWindowBackdrop.GetParent=()=>w.scene.DltSettingsRoot;w.scene.DltSettingsRoot.GetParent=()=>w.host;
  w.owner.style={backgroundColor:options.sceneBackground===undefined?'#000c':options.sceneBackground};w.owner.ClearPropertyFromCode=name=>{if(name==='background-color')delete w.owner.style.backgroundColor;};
  w.owner.FindChildTraverse=id=>w.scene[id]||null;
  const previousFind=w.host.FindChildTraverse;w.host.FindChildTraverse=w.context.FindChildTraverse=id=>w.scene[id]||previousFind(id);
 }
 w.sandbox={String,Date,$:{GetContextPanel:()=>options.contextIsNative?w.nativeChat:w.context,Schedule:(_,fn)=>w.queue.push(fn),DispatchEvent:(name,target)=>w.events.push({name,target}),RegisterEventHandler:(name,panel,fn)=>w.handlers[name]=fn,RegisterForUnhandledEvent:(name,fn)=>{w.handlers[name]=fn;return name},UnregisterForUnhandledEvent:(name,handle)=>w.unregistered.push(name)}};
 vm.runInNewContext(source,w.sandbox);w.tick=()=>{const batch=w.queue.splice(0);for(const fn of batch)fn();};return w;
}
function check(name,fn){if(process.env.DLT_WINDOW_TEST_FILTER&&!new RegExp(process.env.DLT_WINDOW_TEST_FILTER).test(name))return;fn();passed++;console.log('PASS '+name);}
check('owned settings context is published for window-scoped native recording',()=>{const w=world();assert.equal(w.owner.__dltActiveSettingsWindow,w.host);});
check('closing settings clears the native recording scope before panel deletion',()=>{const w=world();w.sandbox.DLTSettingsClose();assert.equal(w.owner.__dltActiveSettingsWindow,null);});
check('loads the in-game endpoint after opening and focuses only the HTML child',()=>{const w=world();assert.equal(w.urls.length,0);w.tick();assert(w.urls[0].includes('/in-game/'));assert(w.focused);assert(!w.urls.some(u=>u.includes('/menu/')));});
check('web readiness reaches the native title bar',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:ready';w.tick();assert(w.status.text.includes('Esc'));});
check('a ready settings window owns an input context before focusing its HTML',()=>{
 const w=world({requireInputContext:true});w.tick();assert.equal(w.focused,false,'navigation-time focus alone is insufficient');
 w.page.title='DLTSETTINGS:ready:1';w.tick();assert.equal(w.activeInputContext,w.host);assert.equal(w.focused,true);
 assert.deepEqual(w.inputContextCalls,[true]);const evidence=w.owner.__dltSettingsInputEvidence;
 assert(evidence.flags&1);assert(evidence.flags&8);assert(evidence.flags&32);assert.equal(evidence.flags&64,0);
 w.sandbox.DLTSettingsFocus();w.tick();assert.deepEqual(w.inputContextCalls,[true],'clicks do not stack modal contexts');
 w.sandbox.DLTSettingsClose();assert.deepEqual(w.inputContextCalls,[true,false]);assert.equal(w.activeInputContext,w.owner);
});
check('unsupported modal context and refused focus remain distinguishable in evidence',()=>{
 const w=world({requireInputContext:true});delete w.host.SetTopOfInputContext;w.tick();w.page.title='DLTSETTINGS:ready:1';w.tick();
 const flags=w.owner.__dltSettingsInputEvidence.flags;assert.equal(flags&1,0);assert(flags&64);assert.equal(flags&8,0);
 assert(w.visible,'unsupported focus never deletes a healthy settings page');
});
check('loaded HTML receives one keyboard handoff without polling focus away from edits',()=>{
 const w=world();let focused=0;w.page.SetFocus=()=>focused++;w.tick();assert.equal(focused,1);
 w.page.title='DLTSETTINGS:ready:1';w.tick();assert.equal(focused,2);
 assert.equal(w.events.filter(e=>e.name==='SetInputFocus'&&e.target===w.page).length,1);
 for(let i=2;i<8;i++){w.handlers.HTMLTitle(w.page,'DLTSETTINGS:ready:'+i);w.tick();}
 assert.equal(focused,2,'periodic status updates cannot steal DOM selection or input');
 w.sandbox.DLTSettingsRetry();w.handlers.HTMLTitle(w.page,'DLTSETTINGS:ready:20');w.tick();assert.equal(focused,4,'a new browser navigation receives a new handoff');
});
check('native form focus requests and clicks only focus the owned live HTML surface',()=>{
 const w=world();let focused=0;w.page.SetFocus=()=>focused++;w.tick();
 w.handlers.HTMLFormFocusPending(w.page);assert.equal(focused,2);
 w.handlers.HTMLFormFocusPending({IsValid:()=>true});assert.equal(focused,2);
 w.sandbox.DLTSettingsFocus();assert.equal(focused,3);
 w.sandbox.DLTSettingsClose();w.sandbox.DLTSettingsFocus();w.handlers.HTMLFormFocusPending(w.page);assert.equal(focused,3);
});
check('clicking already focused HTML never resets its native focus or DOM widget',()=>{
 const w=world();w.tick();w.page.title='DLTSETTINGS:ready:1';w.tick();let calls=0;w.page.SetFocus=()=>{calls++;return true;};
 const before=w.events.filter(e=>e.name==='SetInputFocus').length;
 for(let i=0;i<5;i++){w.sandbox.DLTSettingsFocus();w.handlers.HTMLFormFocusPending(w.page);}
 assert.equal(calls,0);assert.equal(w.events.filter(e=>e.name==='SetInputFocus').length,before);
});
check('HTML descendant focus also preserves an open field selection',()=>{
 const w=world();w.tick();w.page.BHasKeyFocus=()=>false;w.page.BHasDescendantKeyFocus=()=>true;let calls=0;w.page.SetFocus=()=>calls++;
 w.sandbox.DLTSettingsFocus();assert.equal(calls,0);
});
check('settings clicks request input but the hidden bridge cannot accept focus',()=>{
 const settings=fs.readFileSync(path.join(__dirname,'../mod/panorama/layout/dlt_settings_window.xml'),'utf8');
 assert(settings.match(/<HTML[^>]*id="DltSettingsPage"[^>]*>/)[0].includes('onactivate="DLTSettingsFocus();"'));
 const generator=fs.readFileSync(path.join(__dirname,'../scripts/prepare_mod.py'),'utf8');assert(generator.includes('acceptsinput="false" acceptsfocus="false"'),'resource regeneration must retain the bridge input policy');
 const generated=path.join(__dirname,'../mod/panorama/layout/chat.xml');
 // Public CI has no Valve layout; validate the actual generator's inserted HTML.
 // Local builds additionally verify that the generated resource kept the policy.
 for(const xml of [generator,...fs.existsSync(generated)?[fs.readFileSync(generated,'utf8')]:[]]){
  const bridge=xml.match(/<HTML[^>]*id="DltBridge"[^>]*>/)[0];assert(bridge.includes('acceptsinput="false"'));assert(bridge.includes('acceptsfocus="false"'));
 }
});
check('unsupported automatic focus does not stop page loading or close polling',()=>{const w=world();w.page.SetFocus=()=>{throw new Error('not supported')};w.tick();w.page.title='DLTSETTINGS:close';w.tick();assert(!w.visible);});
check('Escape deletes the owned window without starting a new browser navigation',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:close';w.tick();assert(!w.visible);assert.equal(w.urls.length,1);assert.equal(w.events[0].name,'DropInputFocus');});
check('close cancels outstanding polling and is idempotent',()=>{const w=world();w.tick();w.sandbox.DLTSettingsClose();w.sandbox.DLTSettingsClose();w.tick();assert.equal(w.queue.length,0);assert.equal(w.urls.filter(u=>u==='about:blank').length,0);});
check('native escape-menu dismissal destroys the embedded page after confirmation',()=>{const w=world();w.tick();w.escape=false;w.tick();assert(w.visible);w.tick();assert(!w.visible);assert.equal(w.urls.length,1);assert.equal(w.events.length,0);});
check('hidden menu owner also destroys the embedded page',()=>{const w=world();w.tick();w.owner.visible=false;w.tick();assert(!w.visible);});
check('reload invalidates the old poll instead of creating two loops',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:ready:1';w.tick();w.sandbox.DLTSettingsRetry();w.tick();assert.equal(w.queue.length,1);assert.equal(w.urls.length,2);});
check('missing title signal is not reported as proof of a stopped backend',()=>{const w=world();w.tick();for(let i=0;i<41;i++)w.tick();assert(w.status.text.includes('尚未收到页面状态'));assert(!w.status.text.includes('后台已启动'));assert.equal(w.urls.length,1);});
check('clicking the frame never closes an open settings window',()=>{const w=world();w.tick();assert.equal(w.sandbox.DLTSettingsKeepOpen(),true);w.tick();assert(w.visible);assert(!w.urls.includes('about:blank'));});
check('computed visibility changing during HTML focus does not close settings',()=>{const w=world();w.tick();w.owner.BIsVisible=()=>false;w.sandbox.DLTSettingsKeepOpen();w.tick();w.tick();assert(w.visible);});
check('one transient menu-class change does not close settings',()=>{const w=world();w.tick();w.escape=false;w.tick();w.escape=true;w.tick();assert(w.visible);});
check('the decorative backdrop has no click-to-close handler',()=>{const xml=fs.readFileSync(path.join(__dirname,'../mod/panorama/layout/dlt_settings_window.xml'),'utf8');const backdrop=xml.match(/<Panel[^>]*class="DltWindowBackdrop"[^>]*>/)[0];assert(backdrop.includes('hittest="false"'));assert(!backdrop.includes('onactivate'));assert(xml.includes('onactivate="DLTSettingsKeepOpen();"'));});
check('HTMLTitle event works when the title property stays stale',()=>{const w=world();w.tick();w.page.title='static title';w.handlers.HTMLTitle(w.page,'DLTSETTINGS:ready:100');w.tick();assert(w.status.text.includes('设置页已连接'));});
check('GetTitle fallback handles panels without a title property',()=>{const w=world();w.tick();w.page.GetTitle=()=>'DLTSETTINGS:ready:101';w.tick();assert(w.status.text.includes('设置页已连接'));});
check('attribute title fallback is supported',()=>{const w=world();w.tick();w.page.GetAttributeString=()=>'DLTSETTINGS:ready:102';w.tick();assert(w.status.text.includes('设置页已连接'));});
check('disconnect and recovery are distinct page states',()=>{const w=world();w.tick();w.handlers.HTMLTitle('DLTSETTINGS:offline:2');w.tick();assert(w.status.text.includes('连接中断'));w.handlers.HTMLTitle('DLTSETTINGS:ready:3');w.tick();assert(w.status.text.includes('设置页已连接'));});
check('page initialization error is not confused with a missing server',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:error:4';w.tick();assert(w.status.text.includes('初始化失败'));});
check('closing removes global title fallback subscriptions',()=>{const w=world();w.tick();w.sandbox.DLTSettingsClose();assert.deepEqual(w.unregistered.sort(),['HTMLChangedTitle','HTMLTitle']);});
check('the HTML surface explicitly opts into input and focus, not only hit testing',()=>{const xml=fs.readFileSync(path.join(__dirname,'../mod/panorama/layout/dlt_settings_window.xml'),'utf8');const html=xml.match(/<HTML[^>]*id="DltSettingsPage"[^>]*>/)[0];assert(html.includes('acceptsinput="true"'));assert(html.includes('acceptsfocus="true"'));});
check('manual reload alternates only HTTPS certificate-covered loopback hosts after readiness',()=>{const w=world();w.tick();assert(w.urls[0].startsWith('https://localhost:17866/'));w.page.title='DLTSETTINGS:ready:1';w.tick();w.sandbox.DLTSettingsRetry();assert(w.urls[1].startsWith('https://127.0.0.1:17866/'));w.page.title='DLTSETTINGS:ready:2';w.tick();w.sandbox.DLTSettingsRetry();assert(w.urls[2].startsWith('https://localhost:17866/'));});
check('native context deletion releases global subscriptions without touching its HTML',()=>{const w=world();w.tick();w.visible=false;w.handlers.PanelDeleted(w.context);w.tick();assert.deepEqual(w.unregistered.sort(),['HTMLChangedTitle','HTMLTitle']);assert.equal(w.urls.length,1);assert.equal(w.events.length,0);assert.equal(w.queue.length,0);});
check('child deletion does not stop the whole settings window',()=>{const w=world();w.tick();w.handlers.PanelDeleted({});w.tick();assert(w.visible);assert.equal(w.unregistered.length,0);assert.equal(w.queue.length,1);});
check('late title after native destruction only releases subscriptions',()=>{const w=world();w.tick();w.visible=false;w.handlers.HTMLTitle(w.page,'DLTSETTINGS:ready:10');w.tick();assert.equal(w.unregistered.length,2);assert.equal(w.urls.length,1);assert.equal(w.queue.length,0);});
check('native offline help remains readable when HTML is unavailable',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:offline';w.tick();assert(w.help.shown);assert(w.helpText.text.includes('Settings.cmd'));assert(w.helpText.text.includes('原 Windows 用户'));w.page.title='DLTSETTINGS:ready';w.tick();assert(!w.help.shown);});
check('missing title only offers conditional help and does not diagnose administrator mismatch',()=>{const w=world();w.tick();for(let i=0;i<41;i++)w.tick();assert(w.help.shown);assert(w.helpTitle.text.includes('若页面可操作'));assert(!w.helpTitle.text.includes('权限不匹配'));});
check('native SetURL failure shows actionable fallback help',()=>{const w=world();w.page.SetURL=()=>{throw new Error('load failed')};w.tick();assert(w.help.shown);assert(w.helpText.text.includes('重新加载'));});
check('loading settings only requests non-forced chat recovery without resetting its browser',()=>{const w=world(),calls=[];w.owner.__dltAdapterControl={resume:force=>{calls.push(force);return true;}};w.tick();assert.equal(calls[0],false);w.page.title='DLTSETTINGS:ready:20';w.tick();assert(calls.every(force=>force===false));});
check('reconnect title triggers one native recovery instead of closing settings',()=>{const w=world(),calls=[];w.owner.__dltAdapterControl={resume:force=>{calls.push(force);return true;}};w.tick();const before=calls.length;w.handlers.HTMLTitle(w.page,'DLTSETTINGS:reconnect:21');w.tick();w.tick();assert.equal(calls.length,before+1);assert(w.status.text.includes('已请求恢复'));assert(w.visible);});
check('missing recovery support offers VPK instructions without reporting success',()=>{const w=world();w.tick();w.handlers.HTMLTitle(w.page,'DLTSETTINGS:reconnect:22');w.tick();assert(w.status.text.includes('同步更新 VPK'));assert(w.visible);});
check('closing settings arms the original chat owners single delayed guard',()=>{const w=world();let guards=0;w.owner.__dltAdapterControl={afterSettingsClose:()=>guards++};w.tick();w.sandbox.DLTSettingsClose();w.sandbox.DLTSettingsClose();assert.equal(guards,1);assert(!w.visible);});
check('unverified fallback context is never deleted or hidden as a settings window',()=>{const w=world({ownership:false});w.tick();w.sandbox.DLTSettingsClose();assert(w.visible);assert(!w.nativeDeleted);assert.equal(w.owner.__dltActiveSettingsWindow,undefined);});
check('ancestor script context closes only the explicitly created settings host',()=>{const w=world({contextIsNative:true});w.tick();w.sandbox.DLTSettingsClose();assert.equal(!!w.nativeDeleted,false,'native chat must not be deleted');assert(!w.visible);});
check('a marked host from another UI root cannot authorize deletion',()=>{const w=world({contextIsNative:true,foreignHost:true});w.tick();w.sandbox.DLTSettingsClose();assert(w.visible);assert(!w.nativeDeleted);});
const report=path.join(__dirname,'../artifacts/game-window-tests.json');fs.mkdirSync(path.dirname(report),{recursive:true});fs.writeFileSync(report,JSON.stringify({passed,at:new Date().toISOString(),scope:'mock Panorama lifecycle; real game input focus/HTML rendering remains unverified'},null,2));
