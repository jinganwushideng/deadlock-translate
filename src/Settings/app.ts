const inGame = location.pathname.indexOf('/in-game/') === 0 || new URLSearchParams(location.search).get('inGame') === '1';
interface Window { DltSettingsNavigation?: {activate(page:string,section?:string):void;cancel():void;locateElement(node:HTMLElement):void;readonly moving:boolean;readonly page:string}; DltSettingsBackground?: { apply(mode:string,context?:{nativeAvailable?:boolean,previewScene?:boolean}):{requested:string,effective:string};mode():string }; }
let settingsInitialized=false,settingsClosing=false,settingsSignal=0,settingsReloadRequested=false;
const settingsLoadId=new URLSearchParams(location.search).get('windowLoad')||'';
function windowLoadSuffix(){return /^[0-9]{1,9}$/.test(settingsLoadId)?':load:'+settingsLoadId:'';}
let nativeBackgroundAvailable=inGame&&new URLSearchParams(location.search).get('windowProtocol')==='2';
function backgroundSuffix(){return nativeBackgroundAvailable?':background:'+((window.DltSettingsBackground?.mode())||'static'):'';}
function refreshSettingsBackground(){window.DltSettingsBackground?.apply($<HTMLSelectElement>('settingsBackgroundMode').value||'static',{nativeAvailable:inGame&&nativeBackgroundAvailable});}
let gameRecoveryUntil=0,surfaceProbeUntil=0,surfaceProbeSequence=0;
type NativeEditSession={id:string,field:HTMLInputElement|HTMLTextAreaElement,before:string,timer:ReturnType<typeof setTimeout>|null,commitRequested:boolean,geometry:string,completion:Promise<boolean>,complete:(applied:boolean)=>void,resize:ResizeObserver|null};
let nativeEditorSession:NativeEditSession|null=null,nativeEditorOwner='',nativeEditIntent=0;
function publishSettingsState(state:'ready'|'offline'|'error'|'reconnect'){if(settingsReloadRequested)return;if(state==='ready'&&Date.now()<gameRecoveryUntil)return;if(state==='reconnect')gameRecoveryUntil=Date.now()+1200;if(inGame&&!settingsClosing){if(nativeEditorSession?.id)publishNativeGeometry(true);else document.title='DLTSETTINGS:'+(surfaceProbeUntil>Date.now()&&state==='ready'?'surfaceprobe:'+surfaceProbeSequence:state+':'+(++settingsSignal))+backgroundSuffix()+saveSuffix()+windowLoadSuffix();}}
type Term={source:string,target:string,english:string,kind:string,ambiguous:boolean,legacyTarget?:string};
const $=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
type ModelProfile='common'|'incoming'|'outgoing';
let activeModelProfile:ModelProfile='common';
const fields=['settingsBackgroundMode','llmKeepAliveEnabled','llmKeepAliveSeconds','llmIndependentProfiles','incomingModel','outgoingModel','incomingThinking','outgoingThinking','outgoingSeparateBilingual','outgoingSplitLong','incomingTone','incomingCustomTone','incomingShowOwnTranslations','incomingShowHeroName','incomingShowAccountName','incomingHeroNamePosition','incomingOpacity','incomingFeedWidth','incomingFeedPosition','incomingFeedOffsetX','incomingFeedOffsetY','incomingDisplayMode','incomingFeedSeconds','incomingFeedLines','incomingTargetLanguage','incomingCustomLanguage','incomingCustomLanguageCode','outgoingTargetLanguage','outgoingCustomLanguage','outgoingCustomLanguageCode','enabled','incomingScope','mode','fastProvider','azureRegion','llmBaseUrl','model','llmMaxTokens','llmThinkingEnabled','llmThinkingApi','llmStreaming','outgoingUseContext','outgoingContextCount','outgoingContextSeconds','fontSize','bilingualNames','chineseNameStyle','scanIntervalMs','llmConcurrency','fastConcurrency','outgoingConcurrency','outgoingQueueLimit','fastTimeoutSeconds','llmTimeoutSeconds','transport','outgoingEnabled','outgoingHotkey','reserveSendSlot','outgoingMode','outgoingDisplayMode','outgoingTone','outgoingCustomTone','outgoingEnglishCase','outgoingTimeoutSeconds','outgoingFallbackFast'];
type QuickMessage={id:string,text:string,hotkey:string,channel:string,enabled:boolean};
let quickMessages:QuickMessage[]=[];
let terms:Term[]=[];let current:Record<string,unknown>={};let clearAzure=false,clearLlm=false;
function notice(text:string,error=false){$('notice').textContent=text;$('notice').classList.toggle('error',error);}
class ApiError extends Error {constructor(message:string,public status:number,public data?:any){super(message);}}
async function api(path:string,method='GET',data?:unknown){
 const bounded=path==='display-preview'||path.startsWith('native-editor')||method==='GET'&&(['status','diagnostics','config','terms','outgoing'].includes(path)||path.startsWith('hotkey/recording/'));
 const controller=bounded&&typeof AbortController!=='undefined'?new AbortController():null;
 const timer=controller?setTimeout(()=>controller.abort(),5000):null;
 try{const res=await fetch('/api/'+path,{method,signal:controller?.signal,headers:{'Content-Type':'application/json','X-DLT-Request':'1',...(path==='config'&&method==='PUT'&&settingsRevision?{'If-Match':'"'+settingsRevision+'"'}:{})},body:data===undefined?undefined:JSON.stringify(data)});let body:any;try{body=await res.json();}catch{body={};}if(!res.ok)throw new ApiError(body.error||'连接失败，请重新通过设置按钮打开页面',res.status,body);return body;}finally{if(timer!==null)clearTimeout(timer);}
}
function loadFields(data:any){current={settingsBackgroundMode:'static',llmKeepAliveEnabled:false,llmKeepAliveSeconds:120,llmIndependentProfiles:!!(data.settings.incomingModel||data.settings.outgoingModel||data.settings.incomingThinking!=null||data.settings.outgoingThinking!=null),incomingModel:'',outgoingModel:'',incomingThinking:null,outgoingThinking:null,outgoingSeparateBilingual:false,outgoingSplitLong:false,incomingTone:'none',incomingCustomTone:'',incomingShowOwnTranslations:true,incomingShowHeroName:true,incomingShowAccountName:true,incomingHeroNamePosition:'before',incomingOpacity:90,incomingFeedWidth:340,incomingFeedPosition:'center-left',incomingFeedOffsetX:28,incomingFeedOffsetY:0,incomingDisplayMode:'native',incomingFeedSeconds:8,incomingFeedLines:3,outgoingTone:'none',outgoingCustomTone:'',outgoingEnglishCase:'normal',llmMaxTokens:4096,llmThinkingEnabled:true,llmThinkingApi:'auto',llmStreaming:true,outgoingUseContext:false,outgoingContextCount:3,outgoingContextSeconds:30,chineseNameStyle:'current',outgoingHotkey:'LAlt+Space',incomingTargetLanguage:'zh-Hans',incomingCustomLanguage:'',incomingCustomLanguageCode:'',outgoingTargetLanguage:'en',outgoingCustomLanguage:'',outgoingCustomLanguageCode:'',...data.settings};for(const key of fields){const input=$<HTMLInputElement>(key);if(input.type==='checkbox')input.checked=!!current[key];else input.value=current[key]==null?'':String(current[key]);}$<HTMLInputElement>('azureKey').placeholder=data.hasAzureKey?'已保存 · 留空保持不变':'尚未配置';$<HTMLInputElement>('llmKey').placeholder=data.hasLlmKey?'已保存 · 留空保持不变':'尚未配置';quickMessages=Array.isArray(data.settings.quickMessages)?data.settings.quickMessages.map((q:QuickMessage)=>({...q})):[];drawQuickMessages();refreshSettingsBackground();refreshPreview();refreshIncomingDisplay();refreshLanguageFields();syncChordOptions();refreshContextFields();refreshToneFields();refreshIncomingToneFields();refreshModelTabs();rememberLoadedConfig(data);}
const titles:Record<string,string>={overview:'概览',incoming:'接收翻译',outgoing:'发送翻译',shortcuts:'快捷键与短语',providers:'翻译服务',glossary:'游戏词库',appearance:'显示样式',performance:'性能与队列',diagnostics:'诊断与日志'};
let navigationIntent=0;
async function selectPage(page:string,section?:string){
 if(!titles[page])return;const intent=++navigationIntent;window.DltSettingsNavigation?.cancel();
 try{await commitNativeEditor();}catch(e){notice((e as Error).message,true);return;}
 if(intent!==navigationIntent||settingsClosing)return;
 if(page!=='appearance')void stopDisplayPreview();if(page!=='shortcuts')void stopRecording();
 window.DltSettingsNavigation?.activate(page,section);$('pageTitle').textContent=titles[page];
 if(page==='outgoing'||page==='shortcuts')void refreshOutgoing();scheduleAutoSave();
}
document.querySelectorAll<HTMLButtonElement>('nav button[data-page]').forEach(b=>b.addEventListener('click',()=>void selectPage(b.dataset.page!)));
document.querySelectorAll<HTMLButtonElement>('.subnav button').forEach(b=>b.addEventListener('click',()=>void selectPage(b.closest('.nav-group')!.querySelector<HTMLButtonElement>('[data-page]')!.dataset.page!,b.dataset.section)));
document.querySelectorAll<HTMLButtonElement>('[data-go-page]').forEach(b=>b.addEventListener('click',()=>void selectPage(b.dataset.goPage!)));
function bind(id:string,action:()=>Promise<unknown>|void){$(id).addEventListener('click',async()=>{const button=$<HTMLButtonElement>(id);button.disabled=true;try{await action();}catch(e){notice(String((e as Error).message),true);}finally{button.disabled=false;}});}
bind('clearAzure',()=>{clearAzure=true;markSettingsDraft();notice('将自动保存并清除 Azure 密钥。');});bind('clearLlm',()=>{clearLlm=true;markSettingsDraft();notice('将自动保存并清除大模型密钥。');});
bind('test',async()=>{await save();$('testResult').textContent='正在翻译…';const response=await api('test','POST',{direction:$<HTMLSelectElement>('testDirection').value,provider:$<HTMLSelectElement>('testProvider').value,text:$<HTMLInputElement>('testText').value});$('testResult').textContent=response.translation+'\n'+response.elapsedMs+' ms';});
function drawTerms(){
 const search=$<HTMLInputElement>('termSearch').value.toLowerCase();const body=$('termRows');clearChildren(body);
 const matches=terms.map((t,index)=>({t,index})).filter(({t})=>[t.source,t.target,t.legacyTarget||'',t.english].some(s=>s.toLowerCase().includes(search)));
 const labels={source:'原词',target:'当前中文',legacyTarget:'旧中文（可选）',english:'英文全称'};
 for(const {t,index} of matches.slice(0,120)){
  const tr=document.createElement('tr');
  for(const key of ['source','target','legacyTarget','english'] as const){const td=document.createElement('td');const input=document.createElement('input');input.maxLength=key==='source'||key==='english'?100:150;input.value=t[key]||'';input.setAttribute('aria-label',labels[key]);if(key==='legacyTarget')input.placeholder='留空沿用当前名';input.addEventListener('input',()=>t[key]=input.value);td.append(input);tr.append(td);}
  const typeCell=document.createElement('td');const select=document.createElement('select');select.setAttribute('aria-label','类型');
  for(const [v,label] of [['name','专名'],['phrase','短语']]){const o=document.createElement('option');o.value=v;o.textContent=label;select.append(o);}select.value=t.kind;select.addEventListener('change',()=>t.kind=select.value);typeCell.append(select);tr.append(typeCell);
  const td=document.createElement('td');const check=document.createElement('input');check.type='checkbox';check.checked=t.ambiguous;check.setAttribute('aria-label','歧义词');check.addEventListener('change',()=>t.ambiguous=check.checked);td.append(check);tr.append(td);
  const delCell=document.createElement('td');const del=document.createElement('button');del.textContent='×';del.setAttribute('aria-label','删除 '+t.source);del.addEventListener('click',()=>{terms.splice(index,1);drawTerms();markTermsDraft();});delCell.append(del);tr.append(delCell);body.append(tr);
 }
 $('termHint').textContent='共 '+terms.length+' 条；匹配 '+matches.length+' 条，最多显示 120 条。搜索可定位其余词条。';
}
$('termSearch').addEventListener('input',drawTerms);
bind('addTerm',()=>{terms.unshift({source:'',target:'',legacyTarget:'',english:'',kind:'name',ambiguous:false});$<HTMLInputElement>('termSearch').value='';drawTerms();markTermsDraft();});
bind('saveTerms',async()=>{await commitNativeEditor();const snapshot=clone(terms);await api('terms','PUT',snapshot);savedTerms=snapshot;termsDirty=JSON.stringify(terms)!==JSON.stringify(snapshot);notice(termsDirty?'词库已保存，后续修改仍待保存。':'词库已保存，快译缓存已清除。');});
bind('exportTerms',()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(terms,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='deadlock-terms.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
bind('importTerms',()=>$<HTMLInputElement>('termFile').click());
$('termFile').addEventListener('change',async()=>{try{const file=$<HTMLInputElement>('termFile').files?.[0];if(!file)return;if(file.size>2*1024*1024)throw new Error('词库文件过大');const values=JSON.parse(await fileText(file));if(!Array.isArray(values)||values.length>5000||values.some(t=>!t||typeof t.source!=='string'||typeof t.target!=='string'||typeof t.english!=='string'||!['name','phrase'].includes(t.kind)||typeof t.ambiguous!=='boolean'||(t.legacyTarget!==undefined&&(typeof t.legacyTarget!=='string'||t.legacyTarget.length>150))))throw new Error('词库格式不正确');terms=values;drawTerms();markTermsDraft();notice('已载入词库，点击“保存词库”后生效。');}catch(e){notice((e as Error).message,true);}});
bind('stop',async()=>{await api('stop','POST',{});notice('后台已停止。再次使用时请运行启动器。');});
let statusRefreshing=false,outgoingRefreshing=false;
async function refresh(){if(statusRefreshing||settingsClosing)return;statusRefreshing=true;try{const status=await api('status');drawLlmRequests(status.requests?.recent||status.diagnostics?.llmRequests||[]);$('messages').textContent=status.engine.messages;$('pending').textContent=status.engine.pending;if(status.requests){const r=status.requests;$('queueStatus').textContent='快译并发 '+r.fast.active+'/'+r.fast.limit+' · 精翻并发 '+r.llm.active+'/'+r.llm.limit+' · 服务排队 '+(r.fast.waiting+r.llm.waiting)+' · 待发送 '+(status.sending?.pending||0);const receive=status.engine.scheduling;if(receive)$('queueStatus').textContent+=' · 接收'+(receive.burst?'并发':'逐条')+'（未完成 '+receive.unfinished+' / 触发 '+receive.threshold+'）';}$('termCount').textContent=status.terms;$('connection').textContent='后台已连接';renderDiagnostics(status.diagnostics);drawKeepAlive(status.diagnostics?.keepAlive);if(settingsInitialized)publishSettingsState('ready');$('gameStatus').textContent=status.engine.connected?'游戏已连接 · 译文仅对自己显示':'等待游戏连接 · 后台可以先进行试译';const m=status.gameEvidence?.metrics;$('displayStatus').textContent=m?'显示诊断：暂缓 '+(m.hudWaits||0)+' · 重试恢复 '+(m.hudRetryRecovered||0)+' · 原生过期 '+(m.hudNativeExpired||0)+' · 面板销毁 '+(m.hudPanelGone||0)+' · 重试结束 '+(m.hudRetryDropped||0)+' · 独立区写入 '+(m.feedRendered||0)+'（写入不等同于屏幕可见）':'';$('readerStatus').textContent=m?'读取计数：聊天栏 '+(m.regularRows||0)+' / HUD '+(m.hudRows||0)+' / 大厅 '+(m.lobbyRows||0)+(m.hotkeyDrops?'。有 '+(m.hotkeyDrops)+' 次快捷键未能取到聊天输入，可再按一次当前发送快捷键。':m.channelWaits?'。有 '+(m.channelWaits)+' 条消息等待频道确认，原文已保留。':m.channelFaults?'。检测到发送频道可能不一致，已暂停继续发送。':''):'';}catch(e){$('connection').textContent='后台未连接';renderRuntimeIssues(null,e);publishSettingsState('offline');}finally{statusRefreshing=false;}}
Promise.all([api('config'),api('terms')]).then(([c,t])=>{loadFields(c);terms=t;savedTerms=clone(t);drawTerms();settingsInitialized=true;setSaveStatus('所有更改已保存');window.DltSettingsNavigation?.activate('overview');publishSettingsState('ready');refresh();}).catch(e=>{notice(e.message,true);renderRuntimeIssues(null,e);publishSettingsState('error');});

function drawKeepAlive(status:any){const node=document.getElementById('llmKeepAliveStatus');if(!node)return;if(!status){node.textContent='保活状态暂不可用，请确认后台版本。';return;}const labels:Record<string,string>={disabled:'自动保活已关闭',waiting:'等待下次保活',warming:'预热中，真实翻译会优先执行','waiting-for-game':'等待游戏连接',unconfigured:'未配置大模型密钥',unused:'当前未启用大模型翻译',busy:'翻译繁忙，本次保活跳过',cooldown:'翻译服务正在冷却，保活暂缓',succeeded:'预热成功',failed:'保活失败，稍后按退避间隔重试','rate-limited':'服务限流，已延后保活',unauthorized:'密钥或权限错误，保存修正后的设置再试',interrupted:'已给真实翻译让路',cancelled:'保活已取消','configuration-changed':'配置已变更，等待新设置'};node.textContent=(labels[status.state]||'等待保活')+' · 请求 '+(status.requests||0)+' / 成功 '+(status.successes||0)+(status.lastAttemptUtc?' · 最近 '+new Date(status.lastAttemptUtc).toLocaleTimeString()+' / '+status.lastElapsedMs+' ms':'')+(status.httpStatus&&status.httpStatus!==200?' · HTTP '+status.httpStatus:'');}
bind('llmKeepAliveNow',async()=>{const button=$<HTMLButtonElement>('llmKeepAliveNow');button.disabled=true;try{notice('正在按已保存配置预热一次，可能消耗少量 API 额度。');const result=await api('llm/keep-alive','POST',{});drawKeepAlive(result);notice(result.state==='succeeded'?'模型预热完成。':'本次预热结束，请查看保活状态。');}catch(e){notice((e as Error).message,true);}finally{button.disabled=false;}});
type OutgoingItem={waitReason?:string;partStatuses?:{part:number;state:string;submittedAt?:number;observedAt?:number;reason?:string}[];partIndex?:number;partCount?:number;sendCharacters?:number;sendCharacterLimit?:number;truncated?:boolean;omittedCharacters?:number;isPreset?:boolean;translation?:string,targetLanguage?:string,targetLanguageName?:string,id:string,state:string,channel:string,displayMode:string,original:string,english:string,error:string,sendText:string};
type OutgoingRow={row:HTMLElement,state:HTMLElement,original:HTMLElement,english:HTMLElement,reason:HTMLElement,pending:HTMLElement,copySend:HTMLButtonElement,item:OutgoingItem};
const outgoingRows=new Map<string,OutgoingRow>();
function putText(node:HTMLElement,value:string){if(node.textContent!==value)node.textContent=value;}
function drawOutgoing(items:OutgoingItem[]){
 const container=$('outgoingItems'),keep=new Set(items.map(item=>item.id));
 for(const node of Array.from(container.childNodes))if(node.nodeType===3)container.removeChild(node);
 for(const [id,view] of outgoingRows)if(!keep.has(id)){view.row.remove();outgoingRows.delete(id);}
 if(!items.length){if(!container.querySelector('.outgoing-empty')){const empty=document.createElement('p');empty.className='outgoing-empty';empty.textContent='还没有发送任务。';container.append(empty);}return;}
 container.querySelector('.outgoing-empty')?.remove();
 const labels:Record<string,string>={queued:'等待翻译',translating:'翻译中',ready:'等待游戏提交',waiting_input:'等待输入关闭',waiting_channel:'等待频道确认',waiting_order:'等待前一条发送',submitted:'已交给游戏',failed:'未发送'};
 items.slice().reverse().forEach((item,index)=>{
  let view=outgoingRows.get(item.id);
  if(!view){
   const row=document.createElement('div');row.className='outgoing-task';row.style.marginBottom='16px';row.dataset.taskId=item.id;
   view={row,state:document.createElement('strong'),original:document.createElement('div'),english:document.createElement('div'),reason:document.createElement('div'),pending:document.createElement('div'),copySend:document.createElement('button'),item};
   view.reason.className='outgoing-error';const current=view,copy=document.createElement('button');copy.textContent='复制原文';
   copy.addEventListener('click',()=>copyText(current.item.original).then(()=>notice('已复制原文')).catch(()=>notice('无法复制，请直接选择原文复制',true)));
   view.copySend.textContent='复制待发送内容';view.copySend.addEventListener('click',()=>copyText(current.item.sendText).then(()=>notice('已复制待发送内容')).catch(()=>notice('无法复制，请直接选择复制',true)));
   row.append(view.state,view.original,view.english,view.reason,view.pending,copy,view.copySend);outgoingRows.set(item.id,view);
  }
  view.item=item;
  putText(view.state,((item.state==='failed'&&(item.partIndex||0)>0?'部分已提交，剩余停止':labels[item.state])||item.state)+' · '+(item.channel==='party'?'队伍（组队）':item.channel==='team'?'友军':item.channel==='all'?'全体':'未知频道')+(item.displayMode==='bilingual'?' · 原文+译文':' · 仅译文')+(item.isPreset?' · 快捷短语':' · '+(item.targetLanguageName||languageName(item.targetLanguage||'en')))+(Number(item.partCount)>1?' · '+(item.state==='submitted'?'已提交 '+item.partCount+' 条':'当前第 '+((item.partIndex||0)+1)+'/'+item.partCount+' 条'):''));
  putText(view.original,(item.isPreset?'预制内容：':'原文：')+item.original);view.english.hidden=!!item.isPreset;putText(view.english,'译文（'+(item.targetLanguageName||languageName(item.targetLanguage||'en'))+'）：'+((item.translation??item.english)||'（尚未生成）'));
  const failed=!!item.error&&item.state!=='submitted';
  const partStatusLabels:Record<string,string>={pending:'待提交',dispatched:'已交给游戏',echo_observed:'已观察本机回显',echo_unobserved:'未观察到回显'};
  const observation=(item.partStatuses||[]).map(p=>'第 '+(p.part+1)+' 段：'+(partStatusLabels[p.state]||'待提交')+(p.reason==='ambiguous'?'（重复同文，无法唯一对应）':p.reason==='channel-mismatch'?'（频道不一致）':'')).join('；');
  view.reason.className=failed?'outgoing-error':'hint outgoing-truncation';
  putText(view.reason,(failed?'原因：'+item.error:item.truncated?'已按单条上限截取，末尾 '+item.omittedCharacters+' 个字符未发送；完整原文和译文仍保留。开启自动分条可完整续发。':'')+(observation?' '+observation+'；本机观察不代表服务器确认。':''));
  const lengthHint=item.sendText?'（当前段 '+(item.sendCharacters??Array.from(item.sendText).length)+'/'+(item.sendCharacterLimit??80)+' 字符）':'';
  putText(view.pending,(item.state==='failed'?'当前段保留内容：':item.state==='submitted'?'提交内容：':'待发送：')+(item.sendText||'（尚未生成）')+lengthHint);
  view.copySend.hidden=!item.sendText;
  const position=container.children.item(index);if(position!==view.row)container.insertBefore(view.row,position);
 });
}
async function refreshOutgoing(){
 if(outgoingRefreshing||settingsClosing)return;outgoingRefreshing=true;
 try{const data=await api('outgoing');renderHotkey(data.hotkey,data.hotkeyAvailable);drawOutgoing(data.items);}
 catch{$('hotkeyState').textContent='无法获取发送任务，请检查后台连接。';}finally{outgoingRefreshing=false;}
}
function renderDiagnostics(d:any){
 if(!d)return;
 nativeBackgroundAvailable=nativeBackgroundAvailable||d.settingsBackgroundNativeAvailable===true;refreshSettingsBackground();
 putText($('buildStatus'),'后台：'+(d.buildId||d.version)+' · 游戏：'+(d.clientBuildId||d.clientVersion||'尚未连接')+(d.clientBuildId&&d.buildId!==d.clientBuildId?'。构建不一致，请退出游戏后成套覆盖更新。':d.clientBuildId?' · 构建一致':''));
 const perf=d.metrics?.performance;
 if(perf)putText($('performanceStatus'),'查找 '+perf.discoveryMaxMs+' ms · 读取 '+perf.readMaxMs+' ms · 显示 '+perf.renderMaxMs+' ms · 通信 '+perf.bridgeMaxMs+' ms；回调最大 '+perf.windowMaxMs+' ms · ≥8ms '+perf.slowCallbacks+' 次 · 分批延期 '+perf.deferredWork+' 次（最近约 30 秒）');
 const phase:Record<string,string>={'connected':'游戏 HTTPS 通信正常','https-unavailable':'HTTPS 暂不可用，浏览器设置仍可用','certificate-untrusted':'需要信任本机证书并重启 Steam','game-stale':'曾收到游戏请求，目前心跳中断','bridge-no-reply':'游戏已加载桥接页面，但尚未完成回传','waiting-for-game':'尚未收到游戏请求；进入游戏界面后再检查，刚安装证书需重启 Steam'};
 putText($('diagnosticsSummary'),(phase[d.phase]||d.phase)+' · 后台 '+d.version+' / 游戏脚本 '+(d.clientVersion||'尚未上报'));
 if(d.certificate)putText($('certificateStatus'),d.certificate.message+(d.certificate.daysRemaining!=null?'（剩余 '+d.certificate.daysRemaining+' 天）':''));
 if(d.logs)putText($('logStatus'),(d.logs.available?'日志写入正常':'日志写入暂不可用')+' · 最多 '+d.logs.maximumFiles+' 个文件 · 丢弃 '+d.logs.droppedEvents+' 条 · 写入失败 '+d.logs.writeFailures+' 次；导出包不含聊天正文或密钥。');
 renderHotkey(d.hotkey,d.hotkey?.available);renderRuntimeIssues(d);
}
bind('checkConnection',async()=>{renderDiagnostics(await api('diagnostics'));notice('连接检查已更新；没有调用翻译 API。');});
bind('copyDiagnostics',async()=>{const d=await api('diagnostics');await copyText(JSON.stringify(d,null,2));notice('诊断已复制，不含聊天正文或 API 密钥。');});

setInterval(()=>{if(!settingsClosing&&(inGame||!document.hidden)){refresh();if((!$('outgoing').hidden||!$('shortcuts').hidden))refreshOutgoing();}},1000);
document.querySelector('[data-page="outgoing"]')?.addEventListener('click',refreshOutgoing);

function refreshPreview(){
 document.querySelectorAll<HTMLElement>('[data-translation-preview]').forEach(label=>label.style.fontSize=$<HTMLSelectElement>('fontSize').value+'px');
 const legacy=$<HTMLSelectElement>('chineseNameStyle').value==='legacy',bilingual=$<HTMLInputElement>('bilingualNames').checked;
 $('nameStylePreview').textContent='示例：'+(legacy?'神鞭':'劳什')+(bilingual?'（Lash）':'')+'没大招；'+(legacy?'复仇女巫':'薇妲')+(bilingual?'（Vindicta）':'')+'在后面。';
}
$('fontSize').addEventListener('change',refreshPreview);
$('settingsBackgroundMode').addEventListener('change',()=>{refreshSettingsBackground();publishSettingsState('ready');});
function refreshIncomingDisplay(){
 const independent=$<HTMLSelectElement>('incomingDisplayMode').value!=='native';
 $('incomingFeedOptions').hidden=!independent; refreshAppearanceValues();
}
$('incomingDisplayMode').addEventListener('change',refreshIncomingDisplay);
type DisplayDraft={mode:string,opacity:number,width:number,position:string,offsetX:number,offsetY:number,fontSize:number,seconds:number,lines:number,showHeroName:boolean,showAccountName:boolean,heroNamePosition:string,chineseNameStyle:string};
let displayPreviewTimer:ReturnType<typeof setTimeout>|null=null,displayPreviewExpiry:ReturnType<typeof setTimeout>|null=null;
let displayPreviewGeneration=0,displayPreviewStarted=false,displayPreviewQueue=Promise.resolve();
function displayDraft():DisplayDraft{
 const value=(id:string)=>$<HTMLInputElement>(id).value,number=(id:string)=>Number(value(id));
 return {mode:value('incomingDisplayMode'),opacity:number('incomingOpacity'),width:number('incomingFeedWidth'),position:value('incomingFeedPosition'),offsetX:number('incomingFeedOffsetX'),offsetY:number('incomingFeedOffsetY'),fontSize:number('fontSize'),seconds:number('incomingFeedSeconds'),lines:number('incomingFeedLines'),showHeroName:$<HTMLInputElement>('incomingShowHeroName').checked,showAccountName:$<HTMLInputElement>('incomingShowAccountName').checked,heroNamePosition:value('incomingHeroNamePosition'),chineseNameStyle:value('chineseNameStyle')};
}
function refreshAppearanceValues(){
 const o=displayDraft();$('incomingOpacityValue').textContent=o.opacity+'%（越高越不透明）';$('feedWidthValue').textContent=o.width+' px';
}

function requestDisplayPreview(){
 if(settingsClosing)return Promise.resolve();
 const generation=++displayPreviewGeneration,options=displayDraft();displayPreviewStarted=true;
 displayPreviewQueue=displayPreviewQueue.catch(()=>{}).then(async()=>{
  if(generation!==displayPreviewGeneration||settingsClosing)return;
  try{
   const result=await api('display-preview','POST',options);
   if(generation!==displayPreviewGeneration)return;
   if(result.active!==true)throw new Error('后台尚不支持游戏内显示测试，请成套更新。');
   $('displayPreviewStatus').textContent='已请求在游戏实际位置临时测试虚构消息，12 秒后自动关闭；此操作没有保存设置。';
   if(displayPreviewExpiry!==null)clearTimeout(displayPreviewExpiry);
   displayPreviewExpiry=setTimeout(()=>{if(generation===displayPreviewGeneration){displayPreviewStarted=false;$('displayPreviewStatus').textContent='临时测试已结束。继续调整会再次显示；点击保存才会保留这些设置。';}},12000);
  }catch(e){if(generation===displayPreviewGeneration)$('displayPreviewStatus').textContent=(e as Error).message+' 预览没有启动，设置尚未保存。';}
 });return displayPreviewQueue;
}
function scheduleDisplayPreview(){
 refreshAppearanceValues();if(!settingsInitialized||settingsClosing||$('appearance').hidden)return;
 if(displayPreviewTimer!==null)clearTimeout(displayPreviewTimer);
 displayPreviewTimer=setTimeout(()=>{displayPreviewTimer=null;void requestDisplayPreview();},180);
}
async function stopDisplayPreview(){
 if(displayPreviewTimer!==null)clearTimeout(displayPreviewTimer);displayPreviewTimer=null;
 if(displayPreviewExpiry!==null)clearTimeout(displayPreviewExpiry);displayPreviewExpiry=null;
 if(!displayPreviewStarted)return;
 displayPreviewStarted=false;const generation=++displayPreviewGeneration;
 displayPreviewQueue=displayPreviewQueue.catch(()=>{}).then(async()=>{if(generation!==displayPreviewGeneration)return;try{await api('display-preview','DELETE');}catch{} });
 await displayPreviewQueue;$('displayPreviewStatus').textContent='已停止临时测试，未更改已保存设置。';
}
for(const id of ['incomingDisplayMode','incomingShowHeroName','incomingShowAccountName','incomingHeroNamePosition','incomingOpacity','incomingFeedWidth','incomingFeedPosition','incomingFeedOffsetX','incomingFeedOffsetY','incomingFeedSeconds','incomingFeedLines','fontSize','chineseNameStyle']){
 $(id).addEventListener('input',scheduleDisplayPreview);$(id).addEventListener('change',scheduleDisplayPreview);
}
bind('previewDisplay',()=>requestDisplayPreview());bind('stopDisplayPreview',()=>stopDisplayPreview());
window.addEventListener('pagehide',()=>{if(displayPreviewStarted)void fetch('/api/display-preview',{method:'DELETE',keepalive:true,headers:{'X-DLT-Request':'1'}});void stopDisplayPreview();});

$('chineseNameStyle').addEventListener('change',refreshPreview);
$('bilingualNames').addEventListener('change',refreshPreview);

if(inGame){document.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();event.stopPropagation();if(nativeEditorSession){void cancelNativeEditor();return;}void leaveSettings();}});}

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
 session.resize?.disconnect();session.field.classList.remove('native-inline-active');session.complete(applied);
 nativeEditorSession=null;$('cancelNativeInput').hidden=true;$('nativeInputStatus').textContent=message;
 publishSettingsState('ready');if(error)notice(message,true);
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
    finishNativeEditor(session,'文字已更新，等待自动保存。',false,true);scheduleAutoSave();
   }
   try{await api(nativeEditorPath(session.id),'DELETE');}catch{}return;
  }
  if(result.state==='cancelled'){finishNativeEditor(session,'已取消编辑，页面中的原内容保持不变。');return;}
  $('nativeInputStatus').textContent=result.state==='editing'?'正在原位置编辑；移开焦点自动回填，Esc 撤销本次编辑。':'正在接入原位中文输入…';
 }catch(e){
  if(nativeEditorSession!==session)return;
  if(e instanceof ApiError&&e.status===404){finishNativeEditor(session,'原生编辑请求已过期。请确认后台与 VPK 已成套更新，再点击输入框重试。',true);return;}
  $('nativeInputStatus').textContent='暂时无法取得编辑结果，原内容仍保留；可以重试或取消当前编辑。';
 }
 if(nativeEditorSession===session){publishNativeGeometry();session.timer=setTimeout(()=>void pollNativeEditor(session),150);}
}
async function beginNativeEditor(field:HTMLInputElement|HTMLTextAreaElement){
 if(settingsClosing||!settingsInitialized||clipboardInProgress||field.dataset.dltClipboard==='true'||nativeEditorSession?.field===field)return;
 const intent=++nativeEditIntent;
 if(nativeEditorSession)await commitNativeEditor();
 if(intent!==nativeEditIntent||settingsClosing||!field.isConnected||field.disabled||field.readOnly||!field.getClientRects().length||getComputedStyle(field).visibility==='hidden'||Number(getComputedStyle(field).opacity)===0||nativeEditorSession)return;
 let complete!:(applied:boolean)=>void;const completion=new Promise<boolean>(resolve=>{complete=resolve;});
 const session:NativeEditSession={id:'',field,before:field.value,timer:null,commitRequested:false,geometry:'',completion,complete,resize:null};nativeEditorSession=session;
 field.classList.add('native-inline-active');
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
function requestNativeCommitFromPointer(target:EventTarget|null){
 if(!inGame||!nativeEditorSession||target===nativeEditorSession.field)return;
 if(target instanceof Element){
  if(target.closest('#cancelNativeInput,#nativeInputEnabled'))return;
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
document.addEventListener('click',event=>requestNativeCommitFromPointer(event.target),true);
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
$('nativeInputEnabled').addEventListener('change',()=>{if(!$<HTMLInputElement>('nativeInputEnabled').checked)void cancelNativeEditor();});
window.addEventListener('pagehide',()=>{
 const session=nativeEditorSession;if(!session)return;if(session.timer!==null)clearTimeout(session.timer);
 session.resize?.disconnect();session.field.classList.remove('native-inline-active');session.complete(false);nativeEditorSession=null;
 if(session.id)void fetch('/api/'+nativeEditorPath(session.id),{method:'DELETE',keepalive:true,headers:{'X-DLT-Request':'1'}});
});
async function copyText(value:string){
    if(navigator.clipboard&&navigator.clipboard.writeText){try{await navigator.clipboard.writeText(value);return;}catch{}}
    const input=document.createElement('textarea');input.dataset.dltClipboard='true';clipboardInProgress=true;input.value=value;input.style.position='fixed';input.style.opacity='0';
    const previous=document.activeElement as HTMLElement|null;document.body.appendChild(input);input.focus();input.select();
    try{if(!document.execCommand('copy'))throw new Error('Clipboard unavailable');}finally{input.remove();if(previous&&previous.focus)previous.focus();clipboardInProgress=false;}
}
function communityNotice(message:string,error=false){$('communityStatus').textContent=message;$('communityStatus').classList.toggle('error',error);}
async function copyCommunity(value:string,message:string){
 try{await copyText(value);communityNotice(message);}catch{communityNotice('无法自动复制，请手动复制：'+value,true);}
}
bind('copyCommunityGroup',()=>copyCommunity('923443423','已复制 QQ 群号 923443423，欢迎反馈问题、获取及时更新。'));
bind('copyCommunityAuthor',()=>copyCommunity('ともり_一生','已复制 B 站昵称：ともり_一生。'));
bind('copyCommunityAuthorUrl',()=>copyCommunity('https://space.bilibili.com/332089385','已复制作者 B 站主页链接。'));
bind('openCommunityAuthor',async()=>{
 try{await api('community/bilibili','POST',{});communityNotice('已在系统默认浏览器打开作者主页，欢迎点个关注！');}
 catch(e){communityNotice((e as Error).message,true);}
});
function fileText(file:File):Promise<string>{
    if(typeof file.text==='function')return file.text();
    return new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result||''));reader.onerror=()=>reject(new Error('无法读取词库文件'));reader.readAsText(file);});
}

bind('burstPreset',()=>{for(const [id,value] of [['fastConcurrency','4'],['llmConcurrency','4'],['outgoingConcurrency','3'],['outgoingQueueLimit','12']])$<HTMLInputElement>(id).value=value;markSettingsDraft();notice('已填入高频配置：快译 4、精翻 4、发送 3、队列 12。将自动保存。');});

function languageName(id:string){const option=document.querySelector<HTMLOptionElement>('#outgoingTargetLanguage option[value="'+id+'"]');return option?.textContent||id;}
function refreshLanguageFields(){for(const direction of ['incoming','outgoing'])$(direction+'CustomLanguageFields').hidden=$<HTMLSelectElement>(direction+'TargetLanguage').value!=='custom';}
for(const direction of ['incoming','outgoing'])$(direction+'TargetLanguage').addEventListener('change',refreshLanguageFields);

let recordingTarget='';
let recordingHotkey=false,recordingId='',recordingGeneration=0,recordingTimer:ReturnType<typeof setTimeout>|null=null,hotkeyTestBaseline=0,hotkeyTestUntil=0;
function renderHotkey(h:any,available?:boolean){
 if(!h){putText($('hotkeyState'),available?'监听已启动；旧后台未提供详细状态':'快捷键监听未启动，请确认后台版本。');return;}
 const reasons:Record<string,string>={NoChat:'聊天框尚未被识别为打开；先打开输入框再按',StaleBridge:'游戏心跳中断，请检查游戏连接',Injected:'收到模拟按键，已忽略；请用实体键盘测试',ModifiersMismatch:'左右修饰键或额外 Ctrl/Shift 不匹配',Disabled:'发送翻译已关闭'};
 let text=h.label+' · '+(h.available?'监听已安装':'监听未安装，错误码 '+h.registrationError)+' · 触发 '+h.triggered+' / 游戏确认 '+h.acknowledged+' / 确认超时 '+h.ackTimeouts;
 if(h.gameSameUser===false)text+='。游戏属于另一 Windows 用户，证书与密钥不共享；请使用原安装用户启动游戏，不能用管理员兼容重启跨账户接管。';
 else if(h.gameElevated===true&&h.backendElevated===false)text+='。游戏是管理员权限，后台是普通权限，可能收不到按键。可主动选择下方“管理员兼容重启”，并由原 Windows 用户确认 UAC。';
 if(h.lastRejection&&reasons[h.lastRejection])text+='。'+reasons[h.lastRejection];
 if(Date.now()<hotkeyTestUntil)text+=(h.testDetected>hotkeyTestBaseline?'。已检测到快捷键；未发送聊天。':'。检测中：按一次已保存的组合键，未发送聊天。');
 putText($('hotkeyState'),text);
}
bind('exportDiagnostics',async()=>{const result=await api('diagnostics/export','POST',{});notice('诊断包已保存：'+result.file+'；所在文件夹已打开。不含聊天正文或密钥。');});
let backupPlan:any=null,backupBusy=false;
function backupBytes(value:number):string{return value>=1073741824?(value/1073741824).toFixed(2)+' GB':(value/1048576).toFixed(2)+' MB';}
function renderBackups(plan:any){
 backupPlan=plan;
 const summary=$('backupSummary');clearChildren(summary);
 const main=document.createElement('p');main.textContent=`更新备份共 ${plan.backupCount} 份，占用 ${backupBytes(plan.totalBytes)}；可清理 ${plan.cleanupCount} 份，约 ${backupBytes(plan.cleanupBytes)}。`;summary.append(main);
 const kept=document.createElement('p');kept.className='hint';kept.textContent=`保留最近 ${plan.keptCount} 份可回退备份；另有 ${plan.protectedCount} 份历史、失败、未完成或其他用途的目录受到保护。`;summary.append(kept);
 $<HTMLButtonElement>('cleanBackups').disabled=backupBusy||plan.cleanupCount===0;
}
function backupControls(busy:boolean){backupBusy=busy;$<HTMLButtonElement>('inspectBackups').disabled=busy;$<HTMLInputElement>('backupIncludeLegacy').disabled=busy;$<HTMLButtonElement>('cleanBackups').disabled=busy||!backupPlan?.cleanupCount;}
$('backupIncludeLegacy').addEventListener('change',()=>{backupPlan=null;$<HTMLButtonElement>('cleanBackups').disabled=true;$('backupSummary').textContent='清理范围已改变，请重新检查备份占用。';});
$('inspectBackups').addEventListener('click',async()=>{
 if(backupBusy)return;backupControls(true);backupPlan=null;$('backupSummary').textContent='正在检查备份占用…';
 try{renderBackups(await api('backups?includeLegacy='+$<HTMLInputElement>('backupIncludeLegacy').checked));}
 catch(e){$('backupSummary').textContent=(e as Error).message;notice((e as Error).message,true);}finally{backupControls(false);}
});
$('cleanBackups').addEventListener('click',async()=>{
 if(backupBusy||!backupPlan?.cleanupCount)return;
 const plan=backupPlan;backupControls(true);backupPlan=null;$('backupSummary').textContent='正在校验保留点并清理旧备份…';
 try{
  const result=await api('backups/cleanup','POST',{planToken:plan.planToken,includeLegacy:plan.includeLegacy});renderBackups(result.inventory);
  const message=`已清理 ${result.removedCount} 份，释放 ${backupBytes(result.removedBytes)}。`+(result.warnings.length?'部分备份校验或删除失败，已保留，请重新检查。':'');notice(message,result.warnings.length>0);
 }catch(e){$('backupSummary').textContent=(e as Error).message;notice((e as Error).message,true);}finally{backupControls(false);}
});
bind('restartHotkey',async()=>{await api('hotkey/reinitialize','POST',{});notice('已请求重建快捷键监听；没有发送聊天。');});
async function copyAdminLaunch(){const launch=await api('launch-options');$<HTMLInputElement>('adminLaunchCommand').value=launch.admin;try{await copyText(launch.admin);notice('已复制管理员兼容启动项。退出游戏后在 Steam 属性中替换翻译启动前缀；原来的普通游戏参数保留在末尾。');}catch{notice('启动项已显示在下方，可手动选择复制。');}}
bind('copyAdminLaunch',copyAdminLaunch);
async function restartAdmin(){await api('admin-restart','POST',{});notice('等待你确认 Windows UAC。只使用原 Windows 用户；确认后点击窗口“重新加载”，浏览器用户请重新打开 Settings.cmd。旧待发送任务不会补发。');}
bind('adminRestart',restartAdmin);
bind('testHotkey',async()=>{await save();const result=await api('hotkey/test','POST',{});hotkeyTestBaseline=result.count;hotkeyTestUntil=Date.now()+result.seconds*1000;notice('15 秒内按一次已保存的快捷键。浏览器或游戏设置窗口中的检测不会发送聊天。');await refreshOutgoing();});
const chordKeys=[...Array.from({length:26},(_,i)=>String.fromCharCode(65+i)),...Array.from({length:10},(_,i)=>String(i)),'Space','Enter','Tab',...Array.from({length:12},(_,i)=>'F'+(i+1))];
const chordLabels:Record<string,string>={Space:'空格',Enter:'Enter',Tab:'Tab'};
for(const name of chordKeys){const option=document.createElement('option');option.value=name;option.textContent=chordLabels[name]||name;$('chordKey').append(option);}
function syncChordOptions(){
 const parts=$<HTMLInputElement>('outgoingHotkey').value.split('+').map(v=>v.trim());
 for(const name of ['Ctrl','Alt','Shift']){const value=parts.slice(0,-1).find(p=>new RegExp('^(?:L|R)?'+name+'$','i').test(p));$<HTMLSelectElement>('chord'+name).value=value?value.slice(0,-name.length).toUpperCase()+name:'';}
 const key=chordKeys.find(v=>v.toLowerCase()===(parts[parts.length-1]||'').toLowerCase());if(key)$<HTMLSelectElement>('chordKey').value=key;
}
for(const name of ['Ctrl','Alt','Shift','Key'])$('chord'+name).addEventListener('change',()=>{
 void stopRecording();const parts=['Ctrl','Alt','Shift'].map(k=>$<HTMLSelectElement>('chord'+k).value).filter(Boolean);parts.push($<HTMLSelectElement>('chordKey').value);
 $<HTMLInputElement>('outgoingHotkey').value=parts.join('+');markSettingsDraft();notice('已选择 '+parts.join('+')+'，将自动保存。');
});
$('outgoingHotkey').addEventListener('input',()=>{void stopRecording();syncChordOptions();});
async function stopRecording(){
 const id=recordingId;recordingTarget='';recordingId='';recordingHotkey=false;++recordingGeneration;if(recordingTimer!==null)clearTimeout(recordingTimer);recordingTimer=null;putText($('recordHotkey'),'录制快捷键');
 if(id)try{await api('hotkey/recording/'+id,'DELETE');}catch{}
}
async function pollRecording(generation:number,id:string){
 if(generation!==recordingGeneration||settingsClosing)return;
 try{
  const result=await api('hotkey/recording/'+id);
  if(generation!==recordingGeneration)return;
  if(result.state==='completed'){
   if(typeof result.binding!=='string'||!result.binding.length||result.binding.length>64)throw new Error('录制返回无效，请使用按键选项。');
   if(recordingTarget){const item=quickMessages.find(q=>q.id===recordingTarget);if(item)item.hotkey=result.binding;drawQuickMessages();}else{$<HTMLInputElement>('outgoingHotkey').value=result.binding;syncChordOptions();}await stopRecording();markSettingsDraft();notice('已录制 '+result.label+'，将自动保存；没有发送聊天。');return;
  }
  if(!['arming','recording'].includes(result.state)){await stopRecording();notice(result.error||'已取消录制。',!!result.error);return;}
  notice(result.error||(result.state==='arming'?'正在启用录制，请先松开按键…':'请按组合键（剩余 '+result.secondsRemaining+' 秒）；Esc 取消，不发送聊天。'),!!result.error);
  recordingTimer=setTimeout(()=>void pollRecording(generation,id),250);
 }catch(e){if(generation!==recordingGeneration)return;await stopRecording();notice((e as Error).message+' 可直接使用下方按键选项。',true);}
}
async function startRecording(target=''){
 if(recordingHotkey){await stopRecording();notice('已取消录制。');return;}
 recordingTarget=target;const generation=++recordingGeneration;recordingHotkey=true;putText($('recordHotkey'),'取消录制');notice('正在启用录制…');
 try{
  const result=await api('hotkey/recording','POST',{inGame});
  if(typeof result.id!=='string'||!/^\w{32}$/.test(result.id))throw new Error('后台不支持可靠录制，请成套更新后台与 Mod，或使用按键选项。');
  if(generation!==recordingGeneration){await api('hotkey/recording/'+result.id,'DELETE');return;}
  recordingId=result.id;void pollRecording(generation,result.id);
 }catch(e){if(generation!==recordingGeneration)return;await stopRecording();notice((e as Error).message+' 可直接使用下方按键选项。',true);}
}
bind('recordHotkey',()=>startRecording());
bind('resetHotkey',async()=>{await stopRecording();$<HTMLInputElement>('outgoingHotkey').value='LAlt+Space';syncChordOptions();markSettingsDraft();notice('已恢复默认左 Alt＋空格，将自动保存。');});
document.addEventListener('visibilitychange',()=>{if(document.hidden)void stopRecording();});
window.addEventListener('pagehide',()=>void stopRecording());

type HelpAction='reconnect'|'check'|'admin'|'copy-admin'|'outgoing'|'shortcuts'|'providers'|'logs';
type RuntimeIssue={id:string,title:string,detail:string,steps:string[],actions:HelpAction[]};
const runtimeIssueRows=new Map<string,{node:HTMLElement,title:HTMLElement,detail:HTMLElement,steps:HTMLOListElement,actions:HTMLElement,signature:string}>();
function collectRuntimeIssues(d:any,error?:unknown):RuntimeIssue[]{
 const issues:RuntimeIssue[]=[];
 const add=(id:string,title:string,detail:string,steps:string[],actions:HelpAction[])=>issues.push({id,title,detail,steps,actions});
 if(!d){
  if(error instanceof ApiError&&error.status===401)add('session','设置会话已失效','后台可能已重启，这个页面的旧登录凭据已经失效。',['关闭旧设置页，通过游戏“翻译设置”按钮重新打开；浏览器用户重新运行 Settings.cmd。','仅刷新旧浏览器地址可能仍使用旧会话，不需要重装证书。'],[]);
  else if(error instanceof ApiError&&error.status===403)add('request-refused','后台拒绝了当前页面请求','当前设置入口或请求来源不被接受。',['从游戏设置按钮或 Settings.cmd 重新打开，使用程序生成的本机地址。','仍失败时运行 Export-Diagnostics.cmd 导出日志。'],[]);
  else add('offline','设置页与后台连接中断','暂时无法读取后台状态，当前无法确认是不是管理员权限问题。',['重新加载游戏内设置窗口；浏览器用户重新运行 Settings.cmd。','若后台未启动，退出游戏后按正确的 Steam 翻译启动项重新启动。游戏需要管理员权限时，采用管理员兼容启动并用原 Windows 用户确认 UAC。','仍失败时运行 Export-Diagnostics.cmd 导出日志，不要删除密钥或反复安装证书。'],['check']);
  return issues;
 }
 const h=d.hotkey;
 const p=d.gamePrivileges||(h?.gameForeground===true?{detected:true,elevated:h.gameElevated,sameUser:h.gameSameUser}:{});
 const lowBackend=d.backendElevated===false||(d.backendElevated==null&&h?.backendElevated===false);
 const mismatch=p.detected===true&&p.elevated===true&&lowBackend;
 if(p.detected===true&&p.sameUser===false)add('different-user','游戏和翻译后台使用了不同的 Windows 用户','证书与加密密钥属于原安装用户；管理员重启不能解决跨账号问题。',['退出游戏，用原安装用户登录 Windows 并启动 Steam 与翻译程序。','出现 UAC 时使用原 Windows 用户确认，不能填写另一位管理员的账户。','不需要删除配置、API 密钥或重复安装证书。'],['check','logs']);
 else if(mismatch){
  const actions:HelpAction[]=p.sameUser===true?['admin','copy-admin','check']:['outgoing','check'];
  add('privilege','权限不匹配：游戏是管理员，后台是普通权限','这可能导致发送快捷键无法被读取；连接异常需结合下面的检查结果处理。',['推荐：退出游戏，取消 Deadlock 和 Steam 快捷方式的“以管理员身份运行”，再从 Steam 正常启动。','如果游戏必须使用管理员权限，主动选择“管理员兼容重启”，由原 Windows 用户确认 UAC；完成后重新加载设置页。','长期使用可复制管理员兼容启动项，保留普通游戏参数。重启后旧待发送任务不会自动补发。'],actions);
 }
 if(d.clientVersion&&d.version&&d.clientVersion!==d.version)add('version','后台与游戏 Mod 版本不一致','当前后台 '+d.version+'，游戏脚本 '+d.clientVersion+'；部分发送功能可能被拒绝。',['退出 Deadlock，用新版 GUI 安装器执行“安装 / 覆盖更新”，同时更新后台和游戏 VPK。','不要只复制后台 EXE。更新后重新启动游戏，再点击“重新检查”。'],['check','logs']);
 const certificate=d.certificate?.state;
 if(['unavailable','expired','not-yet-valid','untrusted'].includes(certificate)){
  const steps=certificate==='not-yet-valid'?['检查 Windows 日期、时间与时区，校准后重启翻译后台。']:certificate==='untrusted'?['退出游戏，运行 GUI 安装器，在第 1 步确认本程序证书信任。','首次信任后完整退出并重启 Steam，重新加载游戏设置窗口。']:['退出游戏并停止后台，运行 GUI 安装器，在第 1 步修复或续期本程序证书。','用原 Windows 用户操作，完成信任后完整重启 Steam。'];
  add('certificate',certificate==='expired'?'本机 HTTPS 证书已过期':certificate==='untrusted'?'游戏连接需要信任本机证书':certificate==='not-yet-valid'?'本机证书尚未生效':'本机 HTTPS 证书不可用','浏览器设置可能仍能打开，但游戏的 HTTPS 桥接需要有效证书。',steps,['check','logs']);
 }else if(certificate==='trust-check-failed'){
  add('certificate-check','无法读取当前用户的证书信任状态','检查结果未知，不表示证书一定无效。',['使用原 Windows 用户，在 GUI 安装器第 1 步重新检测本程序证书。','不要先删除密钥或反复安装证书；仍失败请导出日志。'],['check','logs']);
 }else if(['game-stale','bridge-no-reply','waiting-for-game'].includes(d.phase)&&p.detected!==false&&(d.phase!=='waiting-for-game'||p.detected===true)){
  add('bridge',d.phase==='game-stale'?'后台正常，游戏聊天连接已中断':'后台正常，游戏聊天暂未接通','设置页能读取后台，但游戏聊天桥接未及时上报；这不代表后台退出，也不能据此判断为管理员权限问题。',['先点击“恢复游戏连接”，等待几秒再重新检查；新脚本会尝试重建通信面板并恢复聊天轮询。','没有恢复时，退出游戏后成套覆盖更新 VPK 和后台，再重新启动游戏。不要反复安装证书。','若仍中断，请记录时间并导出日志；出现权限或证书提示时再按对应提示处理。'],inGame?['reconnect','check','logs']:['check','logs']);
 }
 const sendingEnabled=current.outgoingEnabled!==false;
 if(sendingEnabled&&h?.available===false)add('hook','发送快捷键监听未启动','监听注册错误码：'+(h.registrationError??'未知')+'；当前快捷键可能不会响应。',['打开“快捷键与短语”，点击“重建监听”，再使用“检测快捷键”测试。','确认保存的组合键与实体键盘一致；检查左右 Alt/Ctrl，必要时重新录制。','若上方报告权限不匹配，先按该提示处理。'],['shortcuts','logs']);
 else if(sendingEnabled&&h?.confirmationTimedOut===true)add('ack','快捷键已触发，但游戏没有及时确认','这次快捷键请求没有获得游戏确认，不能据此认定译文已经发出。',['打开聊天输入框后重新测试一次；先确认游戏连接正常。','检查“快捷键与短语”的监听状态与游戏当前频道，必要时重建监听。','旧任务不会自动重放；仍失败请导出日志。'],['shortcuts','check','logs']);
 const s=d.sending;
 if(sendingEnabled&&s&&(s.state==='failed'||s.state==='waiting_channel')&&s.refusalCode!=='disabled'&&s.failureCode!=='Disabled'){
  let title='最近一次发送未完成',detail='原文和已生成的译文保留在“最近发送任务”中。';
  let steps=['打开“发送翻译”查看这条任务的具体原因和保留内容。','确认连接、快捷键与翻译服务正常后再发送；不会自动补发旧任务。'];
  let actions:HelpAction[]=['outgoing','logs'];
  if(s.state==='waiting_channel'||s.failureCode==='UnknownChannel'||s.refusalCode==='channel-fault'){title='发送频道未确认或发生不一致';steps=['重新打开正确频道的聊天框，确认是友军、全体还是队伍/组队，再测试一次。','这条任务不会改发全体；需要时在“最近发送任务”复制保留内容。'];}
  else if(s.refusalCode==='native-close'){title='游戏聊天框没有正常收起';detail='关框确认和重试均未完成，这条消息没有提交；原文已保留。';steps=['按 Esc 关闭聊天框，再打开原频道重新发送。','若仍出现，请导出诊断日志；不需要靠输入第二条来触发旧任务。'];}
  else if(s.refusalCode==='submit-unconfirmed'){title='无法确认游戏是否读取了待发送文字';detail='调用提交后文字没有被游戏读取清空，不能把这次事件算作成功发送。';steps=['先检查游戏聊天记录，避免重复发送。','确认未发出后，在“最近发送任务”复制原文重试；程序不会自动补发这次任务。'];}
  else if(s.failureCode==='Unauthorized'){title='翻译服务拒绝了发送请求';detail='服务返回权限/鉴权错误；这与 Windows 管理员权限是不同的问题。';steps=['在“翻译服务”检查 API 密钥、套餐接口、模型完整名称和账号额度。','用“试译一句”测试服务，修复后再发送。'];actions=['providers','outgoing','logs'];}
  else if(s.failureCode==='ReasoningBudget'){title='模型思考耗尽输出额度，最近一次发送失败';steps=['在翻译服务适当提高模型输出上限（含思考），默认 4096 token。','模型仍保留思考；同时检查精翻和发送超时，保存后重试保留的原文。'];actions=['providers','outgoing','logs'];}
  else if(s.refusalCode==='input-truncated'||s.failureCode==='NativeInputTruncated'){title='游戏输入框截短了文本，已停止发送';detail='写入游戏后读回的内容与完整文本不一致，程序没有提交残缺内容。';steps=['在“发送翻译”开启超长内容自动分条；当前游戏单条最多 80 个 Unicode 字符，空格、标点也计入。','成套更新后台和 VPK 后重试保留的原文；若仍出现，导出诊断日志核对当前游戏输入框限制。'];}
  else if(s.failureCode==='OutputTruncated'){title='模型译文被截断，最近一次发送失败';steps=['在翻译服务适当提高模型输出上限，给思考和最终译文留出空间。','确认原文后重试；程序不会发送截断内容。'];actions=['providers','outgoing','logs'];}
  else if(s.failureCode==='NameMismatch'){title='模型未保留游戏专名，最近一次发送失败';steps=['已先尝试本地名称纠正，必要时最多重新翻译一次；仍未通过则保留原文。','核对词库规范名与外号映射，或调整模型后重试；思考设置不变。'];actions=['providers','outgoing','logs'];}
  else if(s.failureCode==='RateLimit'){title='翻译服务限流，最近一次发送失败';steps=['等待限流恢复，适当降低对应翻译服务的并发数。','检查服务套餐限制，确认后重新发送保留内容。'];actions=['providers','outgoing','logs'];}
  else if(s.failureCode==='QueueFull'){title='发送队列已满';steps=['先等现有任务完成，减少短时间内连续提交。','必要时在“发送翻译”调整并发和队列容量，注意服务限流。'];}
  else if(s.networkError==='NameResolutionError'){title='发送失败：无法解析 API 域名';steps=['核对 API 地址、DNS 与网络代理；这不代表模型名或密钥错误。','网络恢复后重试原文；不需要重新安装 Mod 或本机证书。'];actions=['providers','outgoing','logs'];}
  else if(s.networkError==='SecureConnectionError'){title='发送失败：API HTTPS 连接验证失败';steps=['核对系统时间、远程服务证书和代理配置；不要关闭证书验证。','确认连接测试通过后重试；原文已保留。'];actions=['providers','outgoing','logs'];}
  else if(s.networkError==='ProxyTunnelError'){title='发送失败：代理隧道连接失败';steps=['检查代理是否可用、规则是否覆盖 API 域名，核对服务地址。','代理恢复后重试；反复安装 Mod 或修改输出 token 上限不能修复代理连接。'];actions=['providers','outgoing','logs'];}
  else if(s.networkError==='ResponseEnded'||s.networkError==='InvalidResponse'){title='发送失败：API 响应传输中断';steps=['服务没有传回完整译文，未采用残缺内容，也没有自动重放请求。','检查网络、代理与服务状态后主动重试，原文已保留。'];actions=['providers','outgoing','logs'];}
  else if(s.failureCode==='Timeout'||s.failureCode==='Network'){title=s.failureCode==='Timeout'?'最近一次发送超时':'最近一次发送遇到网络错误';steps=['在“最近发送任务”查看具体原因，再用“翻译服务”的连接测试核对；网络超时和模型输出不足是不同问题。','只对明确尚未建立连接的临时错误重连一次；旧任务不会在恢复后自动补发。'];actions=['providers','outgoing','logs'];}
  add('sending',title,detail,steps,actions);
 }
 for(const issue of Array.isArray(d.providers)?d.providers:[]){
  const name=issue.provider==='bing'?'Bing 免密钥快译':issue.provider==='azure'?'Azure 翻译':issue.provider==='llm'?'大模型精翻':'';
  if(!name)continue;
  const code=Number(issue.httpStatus),redirect=issue.code==='ServiceRedirect';
  let title=name+'返回 HTTP '+code,detail='该翻译服务最近一次请求失败，游戏权限和本地连接需要分别判断。';
  let steps=['在“翻译服务”核对对应服务配置，使用“试译一句”单独测试。','新的实际服务请求成功后，这条提示会自动清除；仅检查游戏连接不能证明服务已恢复。'];
  if(redirect&&issue.provider==='bing'){
   title='Bing 翻译被重定向（HTTP '+code+'）';detail=(issue.stage==='bing-page'?'获取网页凭据时':'调用翻译接口时')+'没有得到可用结果；浏览器能打开网页不代表免密钥接口可用。';
   steps=['在这台电脑的 Bing 翻译网页实际输入一句话测试，查看是否要求验证或登录。','程序已尝试受信任的 Bing 官方翻译地址跳转；验证页、异常地址或循环跳转会明确停止。','持续失败时可自行配置 Azure 或大模型服务，再选相应模式试译；不会自动切换付费服务。'];
  }else if(issue.provider==='bing'&&issue.code==='InvalidData'){title='Bing 没有返回有效翻译内容';detail=issue.stage==='bing-page'?'网页没有提供程序需要的翻译凭据。':'接口响应不是可用的译文。';steps=['在同一台电脑的 Bing 网页实际试译，检查是否出现验证或登录要求。','也可能是网页协议变化或通道暂不可用；可以稍后重试，或自行配置正式 API。'];}
  else if(redirect){title=name+'的接口地址发生跳转（HTTP '+code+'）';detail='返回了跳转响应，程序没有把密钥转交给跳转目标。';steps=['填写服务商的正式 API BaseURL，不能使用登录页、官网首页、控制台或文档地址。','核对接口协议和完整模型 ID，在“试译一句”选择对应引擎测试。'];}
  else if(issue.code==='ModelUnavailable'){title=name+'的模型不存在或未授权';detail='服务明确指向模型 ID 或使用权限。';steps=['在“翻译服务”完整填写接口模型 ID，核对大小写、后缀和隐藏空格。','ID 正确时检查当前密钥所属套餐/项目是否允许使用该模型，点击“测试 API 连接”定位。'];}
  else if(issue.code==='QuotaExceeded'){title=name+'的额度或余额不足';detail='服务返回额度错误，继续修改 Windows 权限不能解决。';steps=['检查对应 API 账号、套餐额度与项目账单限制。','额度恢复后再次测试 API。'];}
  else if(issue.code==='EndpointInvalid'){title=name+'的接口路径或模型未找到';detail='服务没有明确区分地址和模型问题。';steps=['用“检查输入”查看实际请求端点，核对服务商的 Chat Completions 示例。','再核对完整模型 ID，不能只凭 404 认定是哪一项填错。'];}
  else if(issue.code==='ParametersRejected'){title=name+'不接受当前协议或请求参数';steps=['使用 Chat Completions 兼容接口，核对模型要求的参数。','在“翻译服务”使用分项 API 测试查看具体提示。'];}
  else if(issue.code==='RegionMismatch'){title='Azure 区域或资源不匹配';steps=['填写密钥对应 Translator 资源的准确区域。','确认使用的是 Translator 密钥，而不是其他 Azure 服务的密钥。'];}
  else if(issue.provider==='llm'&&issue.code==='InvalidData'){title='大模型 API 响应格式不兼容';steps=['检查地址是否属于 Chat Completions 协议，确认没有填官网、Responses 或 Anthropic 入口。','核对模型输出方式，点击“测试 API 连接”验证。'];}
  else if(code===401||code===403){steps=['检查对应 API 密钥、接口、套餐权限或区域；这是服务鉴权问题，不是 Windows 管理员权限。','修复后单独试译，再测试游戏消息。'];}
  else if(code===429){steps=['等待服务限流恢复，适当降低该服务的并发数。','检查套餐限制后再试译。'];}
  add('provider-'+issue.provider,title,detail,steps,['providers','logs']);
 }
 if(p.detected===true&&(p.elevated==null||p.sameUser==null)&&issues.some(i=>['bridge','hook','ack'].includes(i.id))&&!issues.some(i=>['privilege','different-user'].includes(i.id)))add('unknown-permission','无法确认游戏的权限状态','进程权限查询不可用，暂不能判断是否存在管理员或用户不匹配。',['手动检查 Deadlock、Steam 和后台的启动权限，确保使用同一 Windows 用户。','只有确实需要管理员游戏时才选择管理员兼容启动。'],['outgoing','check']);
 return issues;
}
async function runHelpAction(action:HelpAction){
 if(action==='reconnect'){publishSettingsState('reconnect');notice('恢复请求已交给游戏界面；是否恢复以游戏连接状态为准。');}
 else if(action==='check'){renderDiagnostics(await api('diagnostics'));notice('运行状态已重新检查；没有调用翻译 API。');}
 else if(action==='admin')await restartAdmin();
 else if(action==='copy-admin')await copyAdminLaunch();
 else if(action==='outgoing')selectPage('outgoing');
 else if(action==='shortcuts')selectPage('shortcuts');
 else if(action==='providers')selectPage('providers');
 else{const result=await api('diagnostics/export','POST',{});notice('诊断包已保存：'+result.file+'；所在文件夹已打开。');}
}
function renderRuntimeIssues(d:any,error?:unknown){
 const issues=collectRuntimeIssues(d,error),container=$('runtimeAlertItems');
 const keep=new Set(issues.map(i=>i.id));
 for(const [id,row] of runtimeIssueRows)if(!keep.has(id)){row.node.remove();runtimeIssueRows.delete(id);}
 issues.forEach((issue,index)=>{
  let row=runtimeIssueRows.get(issue.id);
  if(!row){const node=document.createElement('article');node.className='runtime-issue';node.dataset.issue=issue.id;row={node,title:document.createElement('h3'),detail:document.createElement('p'),steps:document.createElement('ol'),actions:document.createElement('div'),signature:''};row.actions.className='runtime-issue-actions';node.append(row.title,row.detail,row.steps,row.actions);runtimeIssueRows.set(issue.id,row);}
  putText(row.title,issue.title);putText(row.detail,issue.detail);
  const signature=JSON.stringify([issue.steps,issue.actions]);
  if(signature!==row.signature){
   clearChildren(row.steps);for(const text of issue.steps){const item=document.createElement('li');item.textContent=text;row.steps.append(item);}
   clearChildren(row.actions);const names:Record<HelpAction,string>={reconnect:'恢复游戏连接',check:'重新检查',admin:'管理员兼容重启（需 UAC）','copy-admin':'复制 Steam 兼容启动项',outgoing:'查看发送任务',shortcuts:'查看快捷键与短语',providers:'查看翻译服务',logs:'一键导出日志'};
   for(const action of issue.actions){const button=document.createElement('button');button.textContent=names[action];button.dataset.helpAction=action;button.addEventListener('click',async()=>{button.disabled=true;try{await runHelpAction(action);}catch(e){notice((e as Error).message,true);}finally{button.disabled=false;}});row.actions.append(button);}
   row.signature=signature;
  }
  const position=container.children.item(index);if(position!==row.node)container.insertBefore(row.node,position);
 });
 $('runtimeAlerts').hidden=issues.length===0;
}

type ApiCheckRow={field:string,level:string,code:string,message:string};
type ApiInputReport={provider:string,ready:boolean,endpoint?:string,modelId?:string,keySource:string,checks:ApiCheckRow[]};
type ApiTestReport={configuration:ApiInputReport,attempted:boolean,outcome:string,code:string,field:string,title:string,detail:string,steps:string[],httpStatus?:number,elapsedMs?:number};
const apiReportGeneration:Record<string,number>={llm:0,azure:0};
const apiFields:Record<string,string[]>={llm:['llmBaseUrl','model','llmKey','llmMaxTokens','llmThinkingEnabled','llmThinkingApi','llmStreaming','incomingModel','outgoingModel','incomingThinking','outgoingThinking','llmIndependentProfiles'],azure:['azureRegion','azureKey']};
function apiDraft(provider:'llm'|'azure'){
 const profile=$<HTMLInputElement>('llmIndependentProfiles').checked?activeModelProfile:'common';
 const selectedModel=profile==='common'?'':$<HTMLInputElement>(profile+'Model').value.trim();
 const selectedThinking=profile==='common'?'':$<HTMLSelectElement>(profile+'Thinking').value;
 const clear=provider==='llm'?clearLlm:clearAzure;
 const key=clear?'':$<HTMLInputElement>(provider==='llm'?'llmKey':'azureKey').value;
 return {provider,key:key.trim()?key:null,useSavedKey:!clear,baseUrl:provider==='llm'?$<HTMLInputElement>('llmBaseUrl').value:undefined,model:provider==='llm'?(selectedModel||$<HTMLInputElement>('model').value):undefined,maxTokens:provider==='llm'?Number($<HTMLInputElement>('llmMaxTokens').value):undefined,thinkingEnabled:provider==='llm'?(selectedThinking===''?$<HTMLInputElement>('llmThinkingEnabled').checked:selectedThinking==='true'):undefined,thinkingApi:provider==='llm'?$<HTMLSelectElement>('llmThinkingApi').value:undefined,streaming:provider==='llm'?$<HTMLInputElement>('llmStreaming').checked:undefined,region:provider==='azure'?$<HTMLInputElement>('azureRegion').value:undefined};
}
function apiFieldId(provider:string,field:string){return field==='key'?(provider==='llm'?'llmKey':'azureKey'):field==='model'&&provider==='llm'?(activeModelProfile==='common'?'model':activeModelProfile+'Model'):field==='address'&&provider==='llm'?'llmBaseUrl':field==='tokens'&&provider==='llm'?'llmMaxTokens':field==='thinking'&&provider==='llm'?'llmThinkingApi':field==='streaming'&&provider==='llm'?'llmStreaming':field==='region'&&provider==='azure'?'azureRegion':'';}
function resetApiReport(provider:string){
 ++apiReportGeneration[provider];for(const id of apiFields[provider])$(id).classList.remove('api-field-error');
 const box=$(provider==='llm'?'llmApiReport':'azureApiReport');
 if(!box.hidden){clearChildren(box);box.dataset.outcome='stale';const text=document.createElement('p');text.textContent='输入已改变，之前的检查/测试结果已失效；请重新检查。';box.append(text);}
}
function drawApiReport(provider:string,result:ApiInputReport|ApiTestReport){
 const test='configuration'in result?result:null;const report=test?test.configuration:result as ApiInputReport;
 const box=$(provider==='llm'?'llmApiReport':'azureApiReport');clearChildren(box);box.hidden=false;box.dataset.outcome=test?.outcome??(report.ready?'checked':'blocked');
 const heading=document.createElement('h4');heading.textContent=(provider==='llm'?modelProfileLabel()+' · ':'')+(test?test.title:report.ready?'本地格式检查通过（尚未联网验证）':'发现需要修改的输入');box.append(heading);
 if(test){const detail=document.createElement('p');detail.textContent=test.detail+(test.httpStatus!=null?' · HTTP '+test.httpStatus:'')+(test.elapsedMs!=null?' · '+Math.round(test.elapsedMs)+' ms':'');box.append(detail);}
 if(report.endpoint){const endpoint=document.createElement('p');endpoint.className='api-endpoint';endpoint.textContent='实际请求端点：'+report.endpoint;box.append(endpoint);}
 const list=document.createElement('ul');
 const names:Record<string,string>={address:'接口地址',model:'模型 ID',key:'API 密钥',region:'Azure 区域',provider:'服务选择',tokens:'输出上限',thinking:'思考开关',streaming:'流式反馈'};
 for(const id of apiFields[provider])$(id).classList.remove('api-field-error');
 for(const check of report.checks){const item=document.createElement('li');item.textContent=(check.level==='error'?'需修改':check.level==='warning'?'请确认':'格式通过')+' · '+(names[check.field]||check.field)+'：'+check.message;list.append(item);const id=apiFieldId(provider,check.field);if(check.level==='error'&&id)$(id).classList.add('api-field-error');}
 box.append(list);
 if(test&&test.steps.length){const steps=document.createElement('ol');for(const text of test.steps){const item=document.createElement('li');item.textContent=text;steps.append(item);}box.append(steps);}
 if(test&&test.outcome==='failed'){const id=apiFieldId(provider,test.field);if(id)$(id).classList.add('api-field-error');}
}
async function checkApiInput(provider:'llm'|'azure',network:boolean){
 const generation=++apiReportGeneration[provider],draft=apiDraft(provider),box=$(provider==='llm'?'llmApiReport':'azureApiReport');box.hidden=false;box.dataset.outcome='pending';putText(box,network?'正在检查输入并测试 API，请稍候…':'正在检查输入格式，没有请求云端服务…');
 try{const result=await api('provider-config/'+(network?'test':'check'),'POST',draft);if(generation===apiReportGeneration[provider])drawApiReport(provider,result);}
 catch(e){if(generation!==apiReportGeneration[provider])return;box.dataset.outcome='failed';putText(box,e instanceof ApiError&&e.status===404?'当前后台没有 API 诊断接口。请用新版完整安装包成套覆盖更新并重启后台。':(e as Error).message);}
}
bind('checkLlmConfig',()=>checkApiInput('llm',false));bind('testLlmConfig',()=>checkApiInput('llm',true));
bind('checkAzureConfig',()=>checkApiInput('azure',false));bind('testAzureConfig',()=>checkApiInput('azure',true));
for(const provider of ['llm','azure'])for(const id of apiFields[provider])$(id).addEventListener('input',()=>{
 if(id==='llmKey'&&$<HTMLInputElement>(id).value.trim())clearLlm=false;
 if(id==='azureKey'&&$<HTMLInputElement>(id).value.trim())clearAzure=false;
 resetApiReport(provider);
 if(provider==='llm'){if(id==='llmIndependentProfiles')refreshModelTabs();else refreshModelSummary();}
});
$('clearLlm').addEventListener('click',()=>resetApiReport('llm'));
$('clearAzure').addEventListener('click',()=>resetApiReport('azure'));

function refreshContextFields(){for(const id of ['outgoingContextCount','outgoingContextSeconds'])$<HTMLInputElement>(id).disabled=!$<HTMLInputElement>('outgoingUseContext').checked;}
$('outgoingUseContext').addEventListener('change',refreshContextFields);
type LlmRequest={id:string,direction:string,phase:string,maxTokens:number,thinkingRequested:boolean,thinkingParameter:boolean|null,streamingRequested:boolean,transport:string,contextCount:number,elapsedMs:number,inputTokens?:number|null,outputTokens?:number|null,reasoningTokens?:number|null,finishReason?:string|null,firstResponseMs?:number|null,firstContentMs?:number|null};
const llmRows=new Map<string,HTMLElement>();
function drawLlmRequests(items:LlmRequest[]){
 const host=$('llmRequests');if(!host)return;
 const phases:Record<string,string>={queued:'等待名额',waiting:'等待服务响应',thinking:'正在思考',generating:'生成译文',validating:'校验译文',done:'已完成',failed:'失败',cancelled:'已取消'};
 const ids=new Set(items.slice(0,12).map(i=>i.id));for(const [id,row] of llmRows)if(!ids.has(id)){row.remove();llmRows.delete(id);}
 if(!items.length){if(!host.querySelector('.llm-empty')){const empty=document.createElement('p');empty.className='llm-empty hint';empty.textContent='还没有精翻请求。词库直接返回或缓存命中不会新增云端请求。';host.append(empty);}return;}
 host.querySelector('.llm-empty')?.remove();const tokens=(n:number|null|undefined)=>n==null?'未提供':String(n);const time=(n:number|null|undefined)=>n==null?'未提供':(n/1000).toFixed(2)+' 秒';
 items.slice(0,12).forEach((item,index)=>{let row=llmRows.get(item.id);if(!row){row=document.createElement('div');row.className='llm-request';row.dataset.requestId=item.id;for(let i=0;i<3;i++)row.append(document.createElement(i===0?'strong':'p'));llmRows.set(item.id,row);}
 const direction=item.direction==='outgoing'?'发送':item.direction==='probe'?'连接测试':'接收';
 putText(row.children[0] as HTMLElement,direction+' · '+(phases[item.phase]||'状态未知')+' · '+time(item.elapsedMs));
 putText(row.children[1] as HTMLElement,'输入 '+tokens(item.inputTokens)+' / 输出 '+tokens(item.outputTokens)+' / 思考 '+tokens(item.reasoningTokens)+' token · 上限 '+item.maxTokens+' · 上下文 '+item.contextCount+' 条');
 putText(row.children[2] as HTMLElement,'思考参数：'+(item.thinkingParameter===true?'已发送开启':item.thinkingParameter===false?'已发送关闭':'跟随服务默认')+' · '+(item.transport==='stream'?'流式':item.transport==='json'?'完整 JSON':'等待响应')+' · 首响应 '+time(item.firstResponseMs)+' · 首译文 '+time(item.firstContentMs)+' · 结束 '+(item.finishReason||'未提供'));
 if(host.children.item(index)!==row)host.insertBefore(row,host.children.item(index));});
}

function refreshToneFields(){const tone=$<HTMLSelectElement>('outgoingTone').value;$('outgoingCustomToneFields').hidden=tone!=='custom';$<HTMLTextAreaElement>('outgoingCustomTone').disabled=tone!=='custom';const language=$<HTMLSelectElement>('outgoingTargetLanguage').value;const code=$<HTMLInputElement>('outgoingCustomLanguageCode').value;$<HTMLSelectElement>('outgoingEnglishCase').disabled=language!=='en'&&!(language==='custom'&&/^en(?:-|$)/i.test(code));}
$('outgoingTone').addEventListener('change',refreshToneFields);
$('outgoingTargetLanguage').addEventListener('change',refreshToneFields);
$('outgoingCustomLanguageCode').addEventListener('input',refreshToneFields);

function refreshIncomingToneFields(){const custom=$<HTMLSelectElement>('incomingTone').value==='custom';$('incomingCustomToneFields').hidden=!custom;$<HTMLTextAreaElement>('incomingCustomTone').disabled=!custom;}
$('incomingTone').addEventListener('change',refreshIncomingToneFields);

function drawQuickMessages(){
 const host=$('quickMessageRows');clearChildren(host);
 quickMessages.forEach((item,index)=>{
  const row=document.createElement('div');row.className='quick-message-row';
  const heading=document.createElement('strong');heading.textContent='短语 '+(index+1);row.append(heading);
  const enabled=document.createElement('input');enabled.type='checkbox';enabled.checked=item.enabled;enabled.setAttribute('aria-label','启用短语 '+(index+1));enabled.addEventListener('change',()=>item.enabled=enabled.checked);row.append(enabled);
  const text=document.createElement('textarea');text.rows=2;text.maxLength=1024;text.value=item.text;text.placeholder='要直接发送的最终内容';text.setAttribute('aria-label','短语内容');text.addEventListener('input',()=>item.text=text.value);row.append(text);
  const controls=document.createElement('div');controls.className='quick-message-controls';
  const chord=document.createElement('input');chord.value=item.hotkey;chord.maxLength=64;chord.placeholder='例如 Ctrl+F1';chord.setAttribute('aria-label','短语快捷键');chord.addEventListener('input',()=>item.hotkey=chord.value);controls.append(chord);
  const channel=document.createElement('select');channel.setAttribute('aria-label','发送频道');for(const [value,label] of [['current','当前频道（上次使用）'],['team','友军'],['all','全体'],['party','队伍（组队）']]){const option=document.createElement('option');option.value=value;option.textContent=label;channel.append(option);}channel.value=item.channel;channel.addEventListener('change',()=>item.channel=channel.value);controls.append(channel);
  const record=document.createElement('button');record.textContent='录制';record.addEventListener('click',()=>void startRecording(item.id));controls.append(record);
  const remove=document.createElement('button');remove.textContent='删除';remove.addEventListener('click',()=>{void stopRecording();quickMessages.splice(index,1);drawQuickMessages();markSettingsDraft();});controls.append(remove);
  row.append(controls);host.append(row);
 });
 $<HTMLButtonElement>('addQuickMessage').disabled=quickMessages.length>=10;
 putText($('quickMessageHint'),'已设置 '+quickMessages.length+' / 10 条；快捷键冲突会在保存时指出。');
}
$('addQuickMessage').addEventListener('click',()=>{
 if(quickMessages.length>=10)return;
 quickMessages.push({id:'q-'+Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,7),text:'',hotkey:Array.from({length:12},(_,i)=>'Ctrl+F'+(i+1)).find(chord=>!quickMessages.some(q=>q.hotkey===chord)&&chord!==$<HTMLInputElement>('outgoingHotkey').value)||'Ctrl+Shift+F1',channel:'current',enabled:true});drawQuickMessages();markSettingsDraft();
});
bind('previewTerms',async()=>{
 const box=$('termPreviewResult');putText(box,'正在本机匹配…');
 const result=await api('terms/preview','POST',{text:$<HTMLTextAreaElement>('termPreviewText').value,direction:$<HTMLSelectElement>('termPreviewDirection').value,provider:$<HTMLSelectElement>('termPreviewProvider').value,bilingual:$<HTMLInputElement>('bilingualNames').checked,nameStyle:$<HTMLSelectElement>('chineseNameStyle').value});
 clearChildren(box);
 const summary=document.createElement('p');summary.textContent=result.exact?'整句命中：'+result.exact:'专名处理后：'+result.prepared;box.append(summary);
 const actions:Record<string,string>={protected:'按专名保护',ambiguous:'有歧义，交给上下文判断',overlap:'与更长词重叠，未单独替换',hint:'作为短语提示'};
 for(const match of result.matches||[]){const line=document.createElement('p');line.textContent=match.source+' → '+match.display+' · '+(actions[match.action]||match.action);const edit=document.createElement('button');edit.textContent='定位词条';edit.addEventListener('click',()=>{$<HTMLInputElement>('termSearch').value=match.source;drawTerms();window.DltSettingsNavigation?.locateElement($('termSearch'));});line.append(edit);box.append(line);}
 if(!result.matches?.length){const empty=document.createElement('p');empty.textContent='未匹配词条，可在下方添加外号或简称。';box.append(empty);}
 const note=document.createElement('p');note.className='hint';note.textContent=result.note;box.append(note);
});

function modelProfileLabel(){return {common:'通用',incoming:'接收',outgoing:'发送'}[activeModelProfile];}
function refreshModelSummary(){
 const profile=activeModelProfile,override=profile==='common'?'':$<HTMLInputElement>(profile+'Model').value.trim();
 const model=override||$<HTMLInputElement>('model').value.trim()||'尚未填写';
 const thinking=profile==='common'?'':$<HTMLSelectElement>(profile+'Thinking').value;
 const effective=thinking===''?$<HTMLInputElement>('llmThinkingEnabled').checked:thinking==='true';
 putText($('modelProfileSummary'),'当前测试：'+modelProfileLabel()+' · 模型 '+model+(profile!=='common'&&!override?'（继承通用）':'')+' · 思考'+(effective?'开启':'关闭')+(profile!=='common'&&thinking===''?'（继承通用）':''));
}
function refreshModelTabs(profile:ModelProfile=activeModelProfile,focus=false){
 const independent=$<HTMLInputElement>('llmIndependentProfiles').checked;
 const next=independent?profile:'common';
 if(next!==activeModelProfile)resetApiReport('llm');
 activeModelProfile=next;
 for(const key of ['common','incoming','outgoing'] as ModelProfile[]){
  const tab=$<HTMLButtonElement>('modelTab-'+key),selected=key===activeModelProfile;
  tab.disabled=key!=='common'&&!independent;tab.setAttribute('aria-selected',String(selected));tab.setAttribute('aria-disabled',String(tab.disabled));tab.tabIndex=selected?0:-1;
  tab.title=tab.disabled?'开启独立配置后可分别设置':'';
  $('modelPanel-'+key).hidden=!selected;
 }
 putText($('modelProfileHelp'),independent?'接收与发送可单独设置；留空或选择继承时使用通用配置。关闭开关会保留已填写内容。':'当前接收与发送共用通用配置。开启右侧开关后，可以分别设置模型和思考。');
 putText($('checkLlmConfig'),'检查'+modelProfileLabel()+'配置');putText($('testLlmConfig'),'测试'+modelProfileLabel()+'连接');
 refreshModelSummary();if(focus)$('modelTab-'+activeModelProfile).focus();
}
for(const tab of Array.from(document.querySelectorAll<HTMLButtonElement>('[data-model-profile]'))){
 tab.addEventListener('click',()=>refreshModelTabs(tab.dataset.modelProfile as ModelProfile));
 tab.addEventListener('keydown',event=>{
  if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
  const tabs=Array.from(document.querySelectorAll<HTMLButtonElement>('[data-model-profile]')).filter(t=>!t.disabled),i=tabs.indexOf(tab);
  const next=event.key==='Home'?0:event.key==='End'?tabs.length-1:(i+(event.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;
  event.preventDefault();refreshModelTabs(tabs[next].dataset.modelProfile as ModelProfile,true);
 });
}
$('llmIndependentProfiles').addEventListener('change',()=>{resetApiReport('llm');refreshModelTabs();});
for(const id of ['incomingThinking','outgoingThinking'])$(id).addEventListener('change',()=>{resetApiReport('llm');refreshModelSummary();});


document.querySelector('.brand')?.addEventListener('click',e=>{e.preventDefault();void selectPage('overview');});

const sceneProbeButton=$<HTMLButtonElement>('sceneSurfaceProbe');
const surfaceProbeProtocol=new URLSearchParams(location.search).get('surfaceProbe');
sceneProbeButton.hidden=!(inGame&&surfaceProbeProtocol==='5');
$('sceneSurfaceProbeStatus').hidden=sceneProbeButton.hidden;
bind('sceneSurfaceProbe',async()=>{
 if(surfaceProbeUntil>Date.now())return;
 const duration=6500;
 await save();surfaceProbeSequence++;surfaceProbeUntil=Date.now()+duration;
 document.documentElement.setAttribute('data-surface-probe','1');
 $('sceneSurfaceProbeStatus').textContent='透景检查中：空白处应透出绿色，随后网页暂时隐藏并显示完整绿底，约六秒后恢复。不会发送聊天。';
 publishSettingsState('ready');
 setTimeout(()=>{surfaceProbeUntil=0;document.documentElement.removeAttribute('data-surface-probe');$('sceneSurfaceProbeStatus').textContent='检查结束，已恢复原背景。若第一阶段仍是黑底、只有第二阶段变绿，请导出日志反馈；无需重复安装证书。';publishSettingsState('ready');},duration);
});
