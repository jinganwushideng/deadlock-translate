type NativeEditSession={id:string,field:HTMLInputElement|HTMLTextAreaElement,before:string,timer:ReturnType<typeof setTimeout>|null,watchdog:ReturnType<typeof setTimeout>|null,commitRequested:boolean,geometry:string,completion:Promise<boolean>,complete:(applied:boolean)=>void,resize:ResizeObserver|null};
let nativeEditorSession:NativeEditSession|null=null,nativeEditorOwner='',nativeEditIntent=0;

function clearChildren(node:HTMLElement){while(node.firstChild)node.removeChild(node.firstChild);}
function nativeEditorPath(id:string){return 'native-editor/'+id+'?owner='+nativeEditorOwner;}
// Only bounded geometry travels through the HTML title. Text stays on the paired
// local channel. Clip against every scrolling ancestor, including the term table.
function nativeFieldGeometry(field:HTMLInputElement|HTMLTextAreaElement):number[]{
 const box=field.getBoundingClientRect(),vw=Math.round(innerWidth),vh=Math.round(innerHeight);
 let left=0,top=0,right=vw,bottom=vh;
 for(let ancestor=field.parentElement;ancestor;ancestor=ancestor.parentElement){
  const style=getComputedStyle(ancestor),rect=ancestor.getBoundingClientRect();
  if(/auto|scroll|hidden|clip/.test(style.overflowX)){left=Math.max(left,rect.left+ancestor.clientLeft);right=Math.min(right,rect.left+ancestor.clientLeft+ancestor.clientWidth);}
  if(/auto|scroll|hidden|clip/.test(style.overflowY)){top=Math.max(top,rect.top+ancestor.clientTop);bottom=Math.min(bottom,rect.top+ancestor.clientTop+ancestor.clientHeight);}
 }
 const style=getComputedStyle(field),px=(value:string,fallback=0)=>Number.parseFloat(value)||fallback;
 const visible=field.isConnected&&!field.disabled&&!field.readOnly&&box.width>0&&box.height>0&&style.visibility!=='hidden';
 if(!visible){right=left;bottom=top;}
 return [Math.max(1,vw),Math.max(1,vh),box.left,box.top,Math.max(1,box.width),Math.max(1,box.height),left,top,Math.max(left,right),Math.max(top,bottom),px(style.fontSize,13)*10,px(style.paddingTop)*10,px(style.paddingRight)*10,px(style.paddingBottom)*10,px(style.paddingLeft)*10,px(style.lineHeight,px(style.fontSize,13)*1.5)*10].map(Math.round);
}
function publishNativeGeometry(force=false){
 const session=nativeEditorSession;if(!session?.id||settingsClosing)return;
 const values=nativeFieldGeometry(session.field);
 const visible=Math.min(values[2]+values[4],values[8])-Math.max(values[2],values[6])>0&&Math.min(values[3]+values[5],values[9])-Math.max(values[3],values[7])>0;
 if(!visible)session.commitRequested=true;
 const geometry=(session.commitRequested?'1:':'0:')+values.join(',');
 if(!force&&geometry===session.geometry)return;
 session.geometry=geometry;document.title='DLTSETTINGS:edit:'+session.id+':'+(++settingsSignal)+':'+geometry+backgroundSuffix()+saveSuffix()+windowLoadSuffix();
}
async function commitNativeEditor(){
 const session=nativeEditorSession;if(!session)return;
 session.commitRequested=true;publishNativeGeometry(true);
 let timeout:ReturnType<typeof setTimeout>|null=null;
 try{
  const applied=await Promise.race([session.completion,new Promise<boolean>((_,reject)=>{timeout=setTimeout(()=>reject(new Error('文字仍在回传，尚未保存。请等待或重试；原生框中的内容仍保留。')),12000);})]);
  if(!applied)throw new Error('当前文字编辑未完成，尚未保存。请确认输入内容后再试。');
 }finally{if(timeout!==null)clearTimeout(timeout);}
}
function finishNativeEditor(session:NativeEditSession,message:string,error=false,applied=false){
 if(nativeEditorSession!==session)return;
 if(session.timer!==null)clearTimeout(session.timer);
 if(session.watchdog!==null)clearTimeout(session.watchdog);
 session.resize?.disconnect();session.field.classList.remove('native-inline-active');session.complete(applied);
 nativeEditorSession=null;$('cancelNativeInput').hidden=true;$('nativeInputStatus').textContent=message;
 publishSettingsState('ready');if(error){showInputRecovery(message);notice(message,true);}
}
async function cancelNativeEditor(){
 const session=nativeEditorSession;if(!session)return;
 finishNativeEditor(session,'已取消编辑，页面中的原内容保持不变。');
 if(session.id)try{await api(nativeEditorPath(session.id),'DELETE');}catch{}
}
async function pollNativeEditor(session:NativeEditSession){
 if(nativeEditorSession!==session||settingsClosing)return;
 try{
  const result=await api(nativeEditorPath(session.id));
  if(nativeEditorSession!==session)return;
  if(result.state==='applied'){
   const field=session.field;
   // A select may disable/hide its former custom field while its final native
   // characters are in flight. Keep that draft; detached/changed/read-only fields
   // still cannot be overwritten by a late result.
   if(!field.isConnected||field.readOnly||field.value!==session.before){
    finishNativeEditor(session,'原输入框已变化，本次编辑未覆盖页面内容；请重新编辑。',true);
   }else if(typeof result.value!=='string'){
    finishNativeEditor(session,'编辑结果无效，原内容保持不变。',true);
   }else{
    field.value=result.value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));
    finishNativeEditor(session,field.closest('#termRows')?'文字已更新，请点击「保存词库」。':'文字已更新，等待自动保存。',false,true);scheduleAutoSave();
   }
   try{await api(nativeEditorPath(session.id),'DELETE');}catch{}return;
  }
  if(result.state==='cancelled'){finishNativeEditor(session,'已取消编辑，页面中的原内容保持不变。');return;}
  if(result.state==='editing'){
   if(session.watchdog!==null){clearTimeout(session.watchdog);session.watchdog=null;}
   session.field.classList.add('native-inline-active');
  }
  $('nativeInputStatus').textContent=result.state==='editing'?'正在原生输入框编辑；移开焦点自动回填，Esc 撤销本次编辑。':'正在接入原位中文输入…';
 }catch(e){
  if(nativeEditorSession!==session)return;
  if(e instanceof ApiError&&e.status===404){finishNativeEditor(session,'原生编辑请求已过期。请确认后台与 VPK 已成套更新，再点击输入框重试。',true);return;}
  $('nativeInputStatus').textContent='暂时无法取得编辑结果，原内容仍保留；可以重试或取消当前编辑。';
  showInputRecovery('输入回传暂时失败，原内容仍保留。可等待恢复，或取消本次原生编辑后改用网页输入/粘贴。');
 }
 if(nativeEditorSession===session){publishNativeGeometry();session.timer=setTimeout(()=>void pollNativeEditor(session),150);}
}
async function beginNativeEditor(field:HTMLInputElement|HTMLTextAreaElement){
 if(settingsClosing||!settingsInitialized||clipboardInProgress||field.dataset.dltClipboard==='true'||nativeEditorSession?.field===field)return;
 const intent=++nativeEditIntent;
 if(nativeEditorSession)await commitNativeEditor();
 if(intent!==nativeEditIntent||settingsClosing||!field.isConnected||field.disabled||field.readOnly||!field.getClientRects().length||getComputedStyle(field).visibility==='hidden'||Number(getComputedStyle(field).opacity)===0||nativeEditorSession)return;
 let complete!:(applied:boolean)=>void;const completion=new Promise<boolean>(resolve=>{complete=resolve;});
 const session:NativeEditSession={id:'',field,before:field.value,timer:null,watchdog:null,commitRequested:false,geometry:'',completion,complete,resize:null};nativeEditorSession=session;
 // Keep the original field readable until the native side actually claims it.
 session.watchdog=setTimeout(()=>{if(nativeEditorSession===session)showInputRecovery('原生输入接管较慢；若看不到编辑框，可取消本次编辑并切换网页输入/粘贴。网页原内容仍保留。');},4000);
 $('cancelNativeInput').hidden=false;$('nativeInputStatus').textContent='正在准备原生输入框…';
 try{
  await stopRecording();if(nativeEditorSession!==session||settingsClosing)return;
  if(!nativeEditorOwner){const random=crypto.getRandomValues(new Uint8Array(16));nativeEditorOwner=Array.from(random,b=>b.toString(16).padStart(2,'0')).join('');}
  const caption=(field.getAttribute('aria-label')||field.labels?.[0]?.textContent||'编辑文本').replace(/\s+/g,' ').trim().slice(0,80)||'编辑文本';
  const maxLength=Math.max(field.value.length,field.maxLength>0?Math.min(field.maxLength,4096):2048);
  const result=await api('native-editor','POST',{owner:nativeEditorOwner,label:caption,value:field.value,multiline:field instanceof HTMLTextAreaElement,maxLength});
  if(!/^[a-f0-9]{48}$/.test(result.id||''))throw new Error('后台不支持原生输入，请成套更新后重试。');
  if(nativeEditorSession!==session||settingsClosing){try{await api(nativeEditorPath(result.id),'DELETE');}catch{}return;}
  session.id=result.id;
  if(typeof ResizeObserver!=='undefined'){session.resize=new ResizeObserver(()=>publishNativeGeometry());session.resize.observe(field);session.resize.observe(document.documentElement);}
  publishNativeGeometry(true);void pollNativeEditor(session);
 }catch(e){finishNativeEditor(session,'无法打开原生输入框：'+(e as Error).message,true);}
}
$('nativeInputHelp').hidden=!inGame;
const nativeInputPreference='dlt.native-input-enabled.v1';
if(inGame){try{$<HTMLInputElement>('nativeInputEnabled').checked=sessionStorage.getItem(nativeInputPreference)!=='off';}catch{}}
function rememberNativeInput(){try{sessionStorage.setItem(nativeInputPreference,$<HTMLInputElement>('nativeInputEnabled').checked?'on':'off');}catch{}}
function showInputRecovery(message?:string){if(!inGame)return;$('inputRecovery').hidden=false;if(message)$('inputRecoveryStatus').textContent=message;}
$('inputRecoveryButton').hidden=!inGame;
bind('inputRecoveryButton',()=>showInputRecovery('原生输入没有反应时，可改用网页输入/粘贴；取消只撤销尚未回填的原生草稿，网页现有内容保留。'));
bind('directSettingsInput',()=>{
 const field=nativeEditorSession?.field;
 $<HTMLInputElement>('nativeInputEnabled').checked=false;rememberNativeInput();void cancelNativeEditor();
 showInputRecovery('已改为网页输入/粘贴；未回填的原生编辑已撤销，网页原内容保留。可尝试右键粘贴；仍不可用时请在程序目录运行 Settings.cmd，用浏览器编辑。');
 if(field?.isConnected)field.focus();
});
bind('restoreNativeInput',()=>{
 $<HTMLInputElement>('nativeInputEnabled').checked=true;rememberNativeInput();
 showInputRecovery('已恢复原生输入，请重新点击文字框。数字、下拉框和密钥框保持原有操作。');
});
function requestNativeCommitFromPointer(target:EventTarget|null){
 if(!inGame||!nativeEditorSession||target===nativeEditorSession.field)return;
 if(target instanceof Element){
  if(target.closest('#cancelNativeInput,#nativeInputEnabled,#inputRecoveryButton,#inputRecovery'))return;
  const label=target.closest('label');if(label?.control===nativeEditorSession.field)return;
 }
 nativeEditorSession.commitRequested=true;publishNativeGeometry(true);
}
document.addEventListener('mousedown',event=>{
 if(!inGame||!settingsInitialized||settingsClosing||!$<HTMLInputElement>('nativeInputEnabled').checked||event.button!==0)return;
 const target=event.target;
 requestNativeCommitFromPointer(target);
 if(!(target instanceof HTMLTextAreaElement)&&!(target instanceof HTMLInputElement&&['text','search','url','email','tel'].includes(target.type)))return;
 if(target.disabled||target.readOnly)return;
 event.preventDefault();event.stopPropagation();void beginNativeEditor(target).catch(e=>notice((e as Error).message,true));
},true);
// The embedded browser can deliver activation/click after a native input owned
// the preceding mouse-down. The click must still finish the old editor.
document.addEventListener('click',event=>{
 requestNativeCommitFromPointer(event.target);
 // Some embedded-browser builds deliver activation without mousedown/focusin.
 const field=event.target;
 if(inGame&&settingsInitialized&&!settingsClosing&&$<HTMLInputElement>('nativeInputEnabled').checked&&event.button===0&&
 (field instanceof HTMLTextAreaElement||field instanceof HTMLInputElement&&['text','search','url','email','tel'].includes(field.type))&&!field.disabled&&!field.readOnly){
  event.preventDefault();void beginNativeEditor(field).catch(error=>notice((error as Error).message,true));
 }
},true);
// Labels and keyboard focus can activate an input without a mousedown on the
// field itself. Route those through the same single native editor transaction.
document.addEventListener('focusin',event=>{
 if(!inGame||!settingsInitialized||settingsClosing||!$<HTMLInputElement>('nativeInputEnabled').checked)return;
 const field=event.target;
 if(field instanceof HTMLTextAreaElement||field instanceof HTMLInputElement&&['text','search','url','email','tel'].includes(field.type)){
  if(!clipboardInProgress&&field.dataset.dltClipboard!=='true'&&!field.disabled&&!field.readOnly)void beginNativeEditor(field).catch(error=>notice((error as Error).message,true));
 }
});
document.addEventListener('scroll',()=>publishNativeGeometry(),true);
window.addEventListener('resize',()=>publishNativeGeometry());
bind('cancelNativeInput',()=>cancelNativeEditor());
$('nativeInputEnabled').addEventListener('change',()=>{rememberNativeInput();if(!$<HTMLInputElement>('nativeInputEnabled').checked)void cancelNativeEditor();});
window.addEventListener('pagehide',()=>{
 const session=nativeEditorSession;if(!session)return;if(session.timer!==null)clearTimeout(session.timer);
 if(session.watchdog!==null)clearTimeout(session.watchdog);
 session.resize?.disconnect();session.field.classList.remove('native-inline-active');session.complete(false);nativeEditorSession=null;
 if(session.id)void fetch('/api/'+nativeEditorPath(session.id),{method:'DELETE',keepalive:true,headers:{'X-DLT-Request':'1'}});
});
