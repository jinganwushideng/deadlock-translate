const fs=require('node:fs'),path=require('node:path'),Module=require('node:module'),assert=require('node:assert/strict');
const fixture=path.join(__dirname,'game-window.test.cjs'),prefix=fs.readFileSync(fixture,'utf8').split('function check(name,fn)')[0];
const moduleFixture=new Module(fixture,module);moduleFixture.filename=fixture;moduleFixture.paths=Module._nodeModulePaths(__dirname);moduleFixture._compile(prefix+'\nmodule.exports=world;',fixture);const baseWorld=moduleFixture.exports;
let passed=0;function check(name,fn){fn();passed++;console.log('PASS '+name);}
function world(){
 const w=baseWorld(),fields={},pageTitle=w.handlers.HTMLTitle;
 w.nativeNow=100000;w.sandbox.Date={now:()=>w.nativeNow};w.commands=[];w.nativeContexts=[];w.browsersCreated=0;
 w.owner.__dltAdapterControl={resume:()=>true};let command=null;
 w.geometry=[1100,780,420,320,500,100,0,0,1100,780,150,100,140,100,140,248];
 w.host.actualuiscale_x=1;w.host.actualuiscale_y=1;w.host.GetPositionWithinWindow=()=>({x:0,y:0});
 w.page.actuallayoutwidth=1100;w.page.actuallayoutheight=780;w.page.GetPositionWithinWindow=()=>({x:128,y:180});
 Object.defineProperty(w.owner,'__dltNativeEditorCommand',{get:()=>command,set:value=>{command=value;if(value)w.commands.push({...value});},configurable:true});
 for(const id of ['DltNativeEditor','DltNativeEditSingle','DltNativeEditMulti','DltNativeEditError','DltNativeEditStatus','DltNativeEditApply','DltNativeEditApplyLabel','DltNativeEditCancel']){
  fields[id]={id,text:'',visible:false,enabled:true,style:{},cls:new Set(),IsValid:()=>w.visible,AddClass(c){this.cls.add(c);},RemoveClass(c){this.cls.delete(c);},SetHasClass(c,on){if(on)this.cls.add(c);else this.cls.delete(c);},SetFocus(){w.nativeFocus=id;return true;},BHasKeyFocus(){return w.nativeFocus===id;},SetMaxChars(value){this.maxchars=value;},SetTopOfInputContext(on){w.nativeContexts.push(on);}};
 }
 const oldFind=w.host.FindChildTraverse;w.host.FindChildTraverse=w.context.FindChildTraverse=id=>fields[id]||oldFind(id);
 w.sandbox.$.CreatePanel=()=>{w.browsersCreated++;throw Error('Editor must reuse the main channel');};
 w.setTitle=title=>{w.page.title=title;pageTitle(w.page,title);};
 w.signal=(id='c'.repeat(48),commit=false)=>w.setTitle('DLTSETTINGS:edit:'+id+':1:'+(commit?'1:':'0:')+w.geometry.join(','));
 w.begin=(id='c'.repeat(48))=>{w.tick();w.signal(id);w.tick();return id;};
 w.reply=(data,sent=w.commands.at(-1))=>{w.owner.__dltNativeEditorReply={id:sent.id,request:sent.request,...data};w.tick();};
 w.ready=()=>{w.begin();w.reply({state:'editing',label:'自定义语气',value:'初始中文\n保留原词',multiline:true,maxLength:600});};
 w.fields=fields;return w;
}
check('an opaque request opens owned native controls using the existing main channel',()=>{
 const w=world(),id=w.begin();assert.equal(w.commands[0].id,id);assert.equal(w.commands[0].action,'claim');assert(!('value'in w.commands[0]));
 assert.equal(w.browsersCreated,0);assert(w.fields.DltNativeEditor.cls.has('ShowEditor'));assert.equal(w.fields.DltNativeEditApply.enabled,false);
});
check('background selection metadata preserves exact native editor geometry and Chinese drafts',()=>{
 const w=world();w.host.SetHasClass=()=>{};w.tick();
 w.setTitle('DLTSETTINGS:edit:'+'d'.repeat(48)+':1:0:'+w.geometry.join(',')+':background:blur');w.tick();
 assert.equal(w.commands.at(-1).action,'claim');
 w.reply({state:'editing',label:'自定义语气',value:'中文草稿',multiline:true,maxLength:600});
 assert.equal(w.fields.DltNativeEditMulti.text,'中文草稿');
 w.fields.DltNativeEditMulti.text='保留中文输入';w.tick();
 assert.equal(w.fields.DltNativeEditMulti.text,'保留中文输入');
});
check('current document load tag preserves native text return and ignores retired geometry',()=>{
 const w=world();w.tick();w.setTitle('DLTSETTINGS:ready:1:background:static:save:clean:load:1');w.tick();
 w.setTitle('DLTSETTINGS:edit:'+'e'.repeat(48)+':2:0:'+w.geometry.join(',')+':background:static:save:dirty:load:1');w.tick();
 assert.equal(w.commands.at(-1).action,'claim');w.reply({state:'editing',label:'模型',value:'中文模型',multiline:false,maxLength:150});
 assert.equal(w.fields.DltNativeEditSingle.text,'中文模型');
 w.fields.DltNativeEditSingle.text='待回填中文';w.setTitle('DLTSETTINGS:edit:'+'e'.repeat(48)+':3:1:'+w.geometry.join(',')+':background:static:save:dirty:load:1');w.tick();w.tick();
 assert.equal(w.commands.at(-1).action,'apply');assert.equal(w.commands.at(-1).value,'待回填中文');
});
check('native multilingual draft uses a multiline TextEntry without changing the page',()=>{
 const w=world();w.ready();assert.equal(w.fields.DltNativeEditMulti.text,'初始中文\n保留原词');assert.equal(w.fields.DltNativeEditMulti.maxchars,600);assert.equal(w.nativeFocus,'DltNativeEditMulti');assert.equal(w.fields.DltNativeEditSingle.visible,false);
 w.fields.DltNativeEditMulti.text='用户正在输入的中文';w.tick();w.tick();assert.equal(w.fields.DltNativeEditMulti.text,'用户正在输入的中文','polling never replaces in-progress text');
});
check('a click back in HTML automatically returns the draft without a modal focus trap',()=>{
 const w=world();w.ready();let htmlFocus=0;w.page.SetFocus=()=>{htmlFocus++;w.nativeFocus='HTML';};w.sandbox.DLTSettingsFocus();w.tick();assert.equal(htmlFocus,1);assert.equal(w.commands.at(-1).action,'apply');assert.equal(w.nativeContexts.length,0);
});
check('confirm sends an exact Unicode draft, waits for acknowledgement and clears owned text',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='用中文玩家口语\n保留否定与阿波罗';w.sandbox.DLTEditorApply();
 assert.equal(w.commands.at(-1).action,'apply');assert.equal(w.commands.at(-1).value,'用中文玩家口语\n保留否定与阿波罗');assert.equal(w.fields.DltNativeEditMulti.enabled,false);
 const before=w.commands.length;w.sandbox.DLTEditorApply();assert.equal(w.commands.length,before,'repeat confirm cannot submit twice');
 w.reply({state:'applied'});assert(!w.fields.DltNativeEditor.cls.has('ShowEditor'));assert.equal(w.fields.DltNativeEditMulti.text,'');assert.equal(w.nativeContexts.length,0);w.tick();assert.equal(w.commands.length,before,'stale request title cannot reopen a completed edit');
});
check('network errors keep native text available for a deliberate retry',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='需要保留的中文';w.sandbox.DLTEditorApply();const old=w.commands.at(-1);w.reply({state:'error',message:'后台连接中断'});
 assert.equal(w.fields.DltNativeEditMulti.text,'需要保留的中文');assert.equal(w.fields.DltNativeEditMulti.enabled,true);assert.equal(w.fields.DltNativeEditApplyLabel.text,'重试回填');assert.equal(w.fields.DltNativeEditError.visible,true);
 w.sandbox.DLTEditorApply();const fresh=w.commands.at(-1);assert(fresh.request>old.request);w.reply({state:'applied'},old);assert(w.fields.DltNativeEditor.cls.has('ShowEditor'),'late old acknowledgement is ignored');
 w.reply({state:'applied'},fresh);assert(!w.fields.DltNativeEditor.cls.has('ShowEditor'));
});
check('cancel discards only the native draft and never closes the settings window',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='未确认的修改';w.sandbox.DLTEditorCancel();assert(w.visible);assert.equal(w.commands.at(-1).action,'cancel');assert(!('value'in w.commands.at(-1)));assert.equal(w.fields.DltNativeEditMulti.text,'');assert(!w.fields.DltNativeEditor.cls.has('ShowEditor'));
});
check('Escape cancels the editor before it can close the settings window',()=>{
 const w=world();w.ready();w.sandbox.DLTSettingsClose('escape');assert(w.visible);assert.equal(w.commands.at(-1).action,'cancel');w.sandbox.DLTSettingsClose('close-button');assert(!w.visible);
});
check('single-line fields stay single-line and use the returned field limit',()=>{
 const w=world();w.begin();w.reply({state:'editing',label:'原词',value:'狼女',multiline:false,maxLength:100});assert.equal(w.fields.DltNativeEditSingle.visible,true);assert.equal(w.fields.DltNativeEditMulti.visible,false);assert.equal(w.fields.DltNativeEditSingle.maxchars,100);assert.equal(w.nativeFocus,'DltNativeEditSingle');
});
check('invalid native metadata cannot silently truncate or replace original content',()=>{
 const w=world();w.begin();w.reply({state:'editing',value:'很长的原始词条',multiline:false,maxLength:2});assert.equal(w.fields.DltNativeEditSingle.text,'');assert(w.fields.DltNativeEditStatus.text.includes('无效'));
});
check('unresponsive claim offers retry or cancel without treating empty text as a draft',()=>{
 const w=world();w.begin();w.nativeNow+=8001;w.tick();assert.equal(w.fields.DltNativeEditApplyLabel.text,'重试输入');w.sandbox.DLTEditorApply();assert.equal(w.commands.at(-1).action,'claim');assert(!('value'in w.commands.at(-1)));
});
check('closing the owned host clears queued editor work and native drafts',()=>{
 const w=world();w.ready();w.sandbox.DLTSettingsClose('close-button');assert.equal(w.fields.DltNativeEditMulti.text,'');assert.equal(w.owner.__dltNativeEditorCommand,null);assert.equal(w.owner.__dltNativeEditorReply,null);assert(!w.visible);
});
check('an unavailable main adapter offers an error without an empty draft or a second browser',()=>{
 const w=world();delete w.owner.__dltAdapterControl;w.begin();assert.equal(w.commands.length,0);assert.equal(w.browsersCreated,0);assert(w.fields.DltNativeEditStatus.text.includes('游戏通信脚本尚未就绪'));assert.equal(w.fields.DltNativeEditSingle.text,'');
});
check('the native editor is a root overlay sibling rather than a child of vertical HTML flow',()=>{
 const xml=fs.readFileSync(path.join(__dirname,'../mod/panorama/layout/dlt_settings_window.xml'),'utf8'),stack=[];let found=false;
 for(const match of xml.matchAll(/<(\/?)([A-Za-z][A-Za-z0-9]*)([^>]*)>/g)){
  if(match[1]){stack.pop();continue;}
  const attrs=match[3],id=/\bid="([^"]+)"/.exec(attrs)?.[1],cls=/\bclass="([^"]+)"/.exec(attrs)?.[1];
  if(id==='DltNativeEditor'){found=true;assert.equal(stack.at(-1)?.cls,'DltWindowOverlay');assert(!stack.some(n=>n.cls==='DltSettingsWindow'));}
  if(!attrs.trimEnd().endsWith('/'))stack.push({id,cls});
 }
 assert(found);
});
check('the native entry is positioned over the exact DOM field without a backdrop',()=>{
 const w=world();w.ready();assert.equal(w.fields.DltNativeEditor.style.position,'128px 180px 0px');
 assert.equal(w.fields.DltNativeEditMulti.style.position,'420px 320px 0px');assert.equal(w.fields.DltNativeEditMulti.style.width,'500px');assert.equal(w.fields.DltNativeEditMulti.style.height,'100px');assert.equal(w.fields.DltNativeEditMulti.style.fontSize,'15px');
 assert.equal(w.fields.DltNativeEditError.visible,false);assert.equal(w.nativeContexts.length,0);
 const xml=fs.readFileSync(path.join(__dirname,'../mod/panorama/layout/dlt_settings_window.xml'),'utf8');assert(xml.match(/<Panel[^>]*id="DltNativeEditor"[^>]*>/)[0].includes('hittest="false"'));assert(!xml.includes('填回设置页'));assert(xml.includes('onblur="DLTEditorBlur();"'));
});
check('physical pixels and window origins are converted to host logical units at nonuniform UI scale',()=>{
 const w=world();w.host.actualuiscale_x=1.5;w.host.actualuiscale_y=2;w.host.GetPositionWithinWindow=()=>[30,80];w.page.GetPositionWithinWindow=()=>[222,440];w.page.actuallayoutwidth=1650;w.page.actuallayoutheight=1560;w.ready();
 assert.equal(w.fields.DltNativeEditor.style.position,'128px 180px 0px');assert.equal(w.fields.DltNativeEditMulti.style.position,'420px 320px 0px');assert.equal(w.fields.DltNativeEditMulti.style.width,'500px');
});
check('scrolling repositions the same native control and cannot overwrite IME text',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='正在输入中文';w.geometry[3]=220;w.signal();w.tick();assert.equal(w.fields.DltNativeEditMulti.style.position,'420px 220px 0px');assert.equal(w.fields.DltNativeEditMulti.text,'正在输入中文');assert.equal(w.commands.length,1);
});
check('nested table clipping keeps the full field size but restricts its native viewport',()=>{
 const w=world();w.geometry[6]=400;w.geometry[7]=300;w.geometry[8]=800;w.geometry[9]=380;w.ready();
 assert.equal(w.fields.DltNativeEditor.style.position,'528px 480px 0px');assert.equal(w.fields.DltNativeEditor.style.width,'400px');assert.equal(w.fields.DltNativeEditor.style.height,'80px');assert.equal(w.fields.DltNativeEditMulti.style.position,'20px 20px 0px');assert.equal(w.fields.DltNativeEditMulti.style.width,'500px');
});
check('save or page-switch signals finish the current field exactly once',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='最后输入的中文';w.signal(undefined,true);w.tick();w.tick();assert.equal(w.commands.at(-1).value,'最后输入的中文');const sent=w.commands.length;w.tick();assert.equal(w.commands.length,sent);
});
check('a field scrolled or hidden out of view returns its existing native text before disappearing',()=>{
 const w=world();w.ready();w.fields.DltNativeEditMulti.text='隐藏前刚输入的中文';w.geometry[4]=1;w.geometry[5]=1;w.geometry[8]=w.geometry[6];w.geometry[9]=w.geometry[7];w.signal(undefined,true);w.tick();w.tick();assert.equal(w.commands.at(-1).value,'隐藏前刚输入的中文');assert(w.fields.DltNativeEditor.cls.has('InlineClipped'));
});
check('a commit requested during claim never steals focus or drops the original value',()=>{
 const w=world();w.begin();w.nativeFocus='other';w.signal(undefined,true);w.reply({state:'editing',value:'原有中文',multiline:false,maxLength:100});w.tick();assert.equal(w.nativeFocus,'other');assert.equal(w.commands.at(-1).action,'apply');assert.equal(w.commands.at(-1).value,'原有中文');
});
check('blur waits for IME commit and ignores an in-field focus return',()=>{
 const w=world();w.ready();w.sandbox.DLTEditorBlur();w.tick();assert.equal(w.commands.length,1);w.nativeFocus='HTML';w.fields.DltNativeEditMulti.text='输入法刚提交的中文';w.sandbox.DLTEditorBlur();w.tick();assert.equal(w.commands.at(-1).value,'输入法刚提交的中文');
});
check('unknown geometry never places an editable field at guessed screen coordinates',()=>{
 const w=world();delete w.page.GetPositionWithinWindow;w.ready();assert(w.fields.DltNativeEditStatus.text.includes('无法定位'));assert.equal(w.commands.at(-1).action,'claim');
});
check('malformed and unbounded title geometry cannot open an editor',()=>{
 const w=world();w.tick();w.geometry[4]=Infinity;w.signal();w.tick();assert.equal(w.commands.length,0);w.geometry[4]=999999;w.signal();w.tick();assert.equal(w.commands.length,0);
});
fs.writeFileSync(path.join(__dirname,'../artifacts/native-editor-window-tests.json'),JSON.stringify({passed,realGame:false,scope:'Mock native TextEntry, broker events, acknowledgement and teardown; IME acceptance requires the actual game.'},null,2));console.log('TOTAL '+passed);
