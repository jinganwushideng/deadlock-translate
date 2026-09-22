const vm=require('node:vm'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const source=fs.readFileSync(path.join(__dirname,'../mod/panorama/scripts/dlt_settings_window.js'),'utf8');let passed=0;
function world(){
 const w={queue:[],urls:[],events:[],handlers:{},unregistered:[],visible:true,escape:true,status:{text:''}};
 w.owner={IsValid:()=>true,visible:true,BHasClass:()=>w.escape,GetParent:()=>null};
 w.page={IsValid:()=>true,title:'',SetURL:url=>w.urls.push(url),SetFocus:()=>w.focused=true};
 w.context={IsValid:()=>w.visible,GetParent:()=>w.owner,FindChildTraverse:id=>id==='DltSettingsPage'?w.page:w.status,DeleteAsync:()=>w.visible=false};
 w.sandbox={String,Date,$:{GetContextPanel:()=>w.context,Schedule:(_,fn)=>w.queue.push(fn),DispatchEvent:(name,target)=>w.events.push({name,target}),RegisterEventHandler:(name,panel,fn)=>w.handlers[name]=fn,RegisterForUnhandledEvent:(name,fn)=>{w.handlers[name]=fn;return name},UnregisterForUnhandledEvent:(name,handle)=>w.unregistered.push(name)}};
 vm.runInNewContext(source,w.sandbox);w.tick=()=>{const batch=w.queue.splice(0);for(const fn of batch)fn();};return w;
}
function check(name,fn){fn();passed++;console.log('PASS '+name);}
check('loads the in-game endpoint after opening and focuses only the HTML child',()=>{const w=world();assert.equal(w.urls.length,0);w.tick();assert(w.urls[0].includes('/in-game/'));assert(w.focused);assert(!w.urls.some(u=>u.includes('/menu/')));});
check('web readiness reaches the native title bar',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:ready';w.tick();assert(w.status.text.includes('Esc'));});
check('unsupported automatic focus does not stop page loading or close polling',()=>{const w=world();w.page.SetFocus=()=>{throw new Error('not supported')};w.tick();w.page.title='DLTSETTINGS:close';w.tick();assert(!w.visible);});
check('Escape title signal unloads and deletes the window',()=>{const w=world();w.tick();w.page.title='DLTSETTINGS:close';w.tick();assert(!w.visible);assert.equal(w.urls.at(-1),'about:blank');assert.equal(w.events[0].name,'DropInputFocus');});
check('close cancels outstanding polling and is idempotent',()=>{const w=world();w.tick();w.sandbox.DLTSettingsClose();w.sandbox.DLTSettingsClose();w.tick();assert.equal(w.queue.length,0);assert.equal(w.urls.filter(u=>u==='about:blank').length,1);});
check('native escape-menu dismissal destroys the embedded page after confirmation',()=>{const w=world();w.tick();w.escape=false;w.tick();assert(w.visible);w.tick();assert(!w.visible);assert.equal(w.urls.at(-1),'about:blank');});
check('hidden menu owner also destroys the embedded page',()=>{const w=world();w.tick();w.owner.visible=false;w.tick();assert(!w.visible);});
check('reload invalidates the old poll instead of creating two loops',()=>{const w=world();w.tick();w.sandbox.DLTSettingsRetry();w.tick();assert.equal(w.queue.length,1);assert.equal(w.urls.length,2);});
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
fs.writeFileSync(path.join(__dirname,'../artifacts/game-window-tests.json'),JSON.stringify({passed,at:new Date().toISOString(),scope:'mock Panorama lifecycle; real game input focus/HTML rendering remains unverified'},null,2));
