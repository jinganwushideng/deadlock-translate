const inGame = location.pathname.indexOf('/in-game/') === 0 || new URLSearchParams(location.search).get('inGame') === '1';
let settingsInitialized=false,settingsClosing=false,settingsSignal=0;
function publishSettingsState(state:'ready'|'offline'|'error'){if(inGame&&!settingsClosing)document.title='DLTSETTINGS:'+state+':'+(++settingsSignal);}
type Term={source:string,target:string,english:string,kind:string,ambiguous:boolean};
const $=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
const fields=['enabled','incomingScope','mode','fastProvider','azureRegion','llmBaseUrl','model','fontSize','bilingualNames','scanIntervalMs','llmConcurrency','fastConcurrency','outgoingConcurrency','outgoingQueueLimit','fastTimeoutSeconds','llmTimeoutSeconds','transport','outgoingEnabled','reserveSendSlot','outgoingMode','outgoingDisplayMode','outgoingTimeoutSeconds','outgoingFallbackFast'];
let terms:Term[]=[];let current:Record<string,unknown>={};let clearAzure=false,clearLlm=false;
function notice(text:string,error=false){$('notice').textContent=text;$('notice').classList.toggle('error',error);}
async function api(path:string,method='GET',data?:unknown){const res=await fetch('/api/'+path,{method,headers:{'Content-Type':'application/json','X-DLT-Request':'1'},body:data===undefined?undefined:JSON.stringify(data)});let body:any;try{body=await res.json();}catch{body={};}if(!res.ok)throw new Error(body.error||'连接失败，请重新通过设置按钮打开页面');return body;}
function loadFields(data:any){current=data.settings;for(const key of fields){const input=$<HTMLInputElement>(key);if(input.type==='checkbox')input.checked=!!current[key];else input.value=String(current[key]);}$<HTMLInputElement>('azureKey').placeholder=data.hasAzureKey?'已保存 · 留空保持不变':'尚未配置';$<HTMLInputElement>('llmKey').placeholder=data.hasLlmKey?'已保存 · 留空保持不变':'尚未配置';refreshPreview();}
async function save(){const value={...current};for(const key of fields){const input=$<HTMLInputElement>(key);value[key]=input.type==='checkbox'?input.checked:typeof current[key]==='number'?Number(input.value):input.value;}for(const [key,clear] of [['azureKey',clearAzure],['llmKey',clearLlm]] as const){const input=$<HTMLInputElement>(key);if(clear)value[key]='';else if(input.value.trim())value[key]=input.value.trim();else delete value[key];}const data=await api('config','PUT',value);loadFields(data);$<HTMLInputElement>('azureKey').value='';$<HTMLInputElement>('llmKey').value='';clearAzure=false;clearLlm=false;notice('设置已保存，下一批消息开始使用新设置。');$('saveState').textContent='已保存 · '+new Date().toLocaleTimeString();}
const titles:Record<string,string>={overview:'聊天翻译',providers:'翻译服务',outgoing:'发送翻译',glossary:'游戏词库',appearance:'显示与性能'};
document.querySelectorAll<HTMLButtonElement>('nav button').forEach(b=>b.addEventListener('click',()=>{document.querySelectorAll('.page').forEach(p=>(p as HTMLElement).hidden=p.id!==b.dataset.page);document.querySelectorAll('nav button').forEach(n=>n.classList.toggle('active',n===b));$('pageTitle').textContent=titles[b.dataset.page!];}));
function bind(id:string,action:()=>Promise<unknown>|void){$(id).addEventListener('click',async()=>{const button=$<HTMLButtonElement>(id);button.disabled=true;try{await action();}catch(e){notice(String((e as Error).message),true);}finally{button.disabled=false;}});}
bind('save',save);
bind('clearAzure',()=>{clearAzure=true;notice('保存设置后将清除 Azure 密钥。');});bind('clearLlm',()=>{clearLlm=true;notice('保存设置后将清除大模型密钥。');});
bind('test',async()=>{await save();$('testResult').textContent='正在翻译…';const response=await api('test','POST',{provider:$<HTMLSelectElement>('testProvider').value,text:$<HTMLInputElement>('testText').value});$('testResult').textContent=response.translation+'\n'+response.elapsedMs+' ms';});
function drawTerms(){const search=$<HTMLInputElement>('termSearch').value.toLowerCase();const body=$('termRows');clearChildren(body);const matches=terms.map((t,index)=>({t,index})).filter(({t})=>[t.source,t.target,t.english].some(s=>s.toLowerCase().includes(search)));for(const {t,index} of matches.slice(0,120)){const tr=document.createElement('tr');for(const key of ['source','target','english'] as const){const td=document.createElement('td');const input=document.createElement('input');input.value=t[key];input.setAttribute('aria-label',key);input.addEventListener('input',()=>t[key]=input.value);td.append(input);tr.append(td);}const typeCell=document.createElement('td');const select=document.createElement('select');select.setAttribute('aria-label','类型');for(const [v,label] of [['name','专名'],['phrase','短语']]){const o=document.createElement('option');o.value=v;o.textContent=label;select.append(o);}select.value=t.kind;select.addEventListener('change',()=>t.kind=select.value);typeCell.append(select);tr.append(typeCell);const td=document.createElement('td');const check=document.createElement('input');check.type='checkbox';check.checked=t.ambiguous;check.setAttribute('aria-label','歧义词');check.addEventListener('change',()=>t.ambiguous=check.checked);td.append(check);tr.append(td);const delCell=document.createElement('td');const del=document.createElement('button');del.textContent='×';del.setAttribute('aria-label','删除 '+t.source);del.addEventListener('click',()=>{terms.splice(index,1);drawTerms();});delCell.append(del);tr.append(delCell);body.append(tr);}$('termHint').textContent='共 '+terms.length+' 条；匹配 '+matches.length+' 条，最多显示 120 条。搜索可定位其余词条。';}
$('termSearch').addEventListener('input',drawTerms);
bind('addTerm',()=>{terms.unshift({source:'',target:'',english:'',kind:'name',ambiguous:false});$<HTMLInputElement>('termSearch').value='';drawTerms();});
bind('saveTerms',async()=>{await api('terms','PUT',terms);notice('词库已保存，快译缓存已清除。');});
bind('exportTerms',()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(terms,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='deadlock-terms.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
bind('importTerms',()=>$<HTMLInputElement>('termFile').click());
$('termFile').addEventListener('change',async()=>{try{const file=$<HTMLInputElement>('termFile').files?.[0];if(!file)return;if(file.size>2*1024*1024)throw new Error('词库文件过大');const values=JSON.parse(await fileText(file));if(!Array.isArray(values)||values.length>5000||values.some(t=>!t||typeof t.source!=='string'||typeof t.target!=='string'||typeof t.english!=='string'||!['name','phrase'].includes(t.kind)||typeof t.ambiguous!=='boolean'))throw new Error('词库格式不正确');terms=values;drawTerms();notice('已载入词库，点击“保存词库”后生效。');}catch(e){notice((e as Error).message,true);}});
bind('stop',async()=>{await api('stop','POST',{});notice('后台已停止。再次使用时请运行启动器。');});
let statusRefreshing=false,outgoingRefreshing=false;
async function refresh(){if(statusRefreshing||settingsClosing)return;statusRefreshing=true;try{const status=await api('status');$('messages').textContent=status.engine.messages;$('pending').textContent=status.engine.pending;if(status.requests){const r=status.requests;$('queueStatus').textContent='快译并发 '+r.fast.active+'/'+r.fast.limit+' · 精翻并发 '+r.llm.active+'/'+r.llm.limit+' · 服务排队 '+(r.fast.waiting+r.llm.waiting)+' · 待发送 '+(status.sending?.pending||0);}$('termCount').textContent=status.terms;$('connection').textContent='后台已连接';renderDiagnostics(status.diagnostics);if(settingsInitialized)publishSettingsState('ready');$('gameStatus').textContent=status.engine.connected?'游戏已连接 · 译文仅对自己显示':'等待游戏连接 · 后台可以先进行试译';const m=status.gameEvidence?.metrics;$('readerStatus').textContent=m?'读取计数：聊天栏 '+(m.regularRows||0)+' / HUD '+(m.hudRows||0)+' / 大厅 '+(m.lobbyRows||0)+(m.hotkeyDrops?'。有 '+(m.hotkeyDrops)+' 次快捷键未能取到聊天输入，可再按一次左 Alt＋空格。':m.channelWaits?'。有 '+(m.channelWaits)+' 条消息等待频道确认，原文已保留。':m.channelFaults?'。检测到发送频道可能不一致，已暂停继续发送。':''):'';}catch{$('connection').textContent='后台未连接';publishSettingsState('offline');}finally{statusRefreshing=false;}}
Promise.all([api('config'),api('terms')]).then(([c,t])=>{loadFields(c);terms=t;drawTerms();settingsInitialized=true;publishSettingsState('ready');refresh();}).catch(e=>{notice(e.message,true);publishSettingsState('error');});
type OutgoingItem={id:string,state:string,channel:string,displayMode:string,original:string,english:string,error:string,sendText:string};
type OutgoingRow={row:HTMLElement,state:HTMLElement,original:HTMLElement,english:HTMLElement,reason:HTMLElement,pending:HTMLElement,copySend:HTMLButtonElement,item:OutgoingItem};
const outgoingRows=new Map<string,OutgoingRow>();
function putText(node:HTMLElement,value:string){if(node.textContent!==value)node.textContent=value;}
function drawOutgoing(items:OutgoingItem[]){
 const container=$('outgoingItems'),keep=new Set(items.map(item=>item.id));
 for(const [id,view] of outgoingRows)if(!keep.has(id)){view.row.remove();outgoingRows.delete(id);}
 if(!items.length){if(!container.querySelector('.outgoing-empty')){const empty=document.createElement('p');empty.className='outgoing-empty';empty.textContent='还没有发送任务。';container.append(empty);}return;}
 container.querySelector('.outgoing-empty')?.remove();
 const labels:Record<string,string>={queued:'等待翻译',translating:'翻译中',ready:'等待游戏提交',waiting_channel:'等待频道确认',waiting_order:'等待前一条发送',submitted:'已交给游戏',failed:'未发送'};
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
  putText(view.state,(labels[item.state]||item.state)+' · '+(item.channel==='party'?'队伍（组队）':item.channel==='team'?'友军':item.channel==='all'?'全体':'未知频道')+(item.displayMode==='bilingual'?' · 原文+英文':' · 仅英文'));
  putText(view.original,'原文：'+item.original);putText(view.english,'英文：'+(item.english||'（尚未生成）'));
  putText(view.reason,item.error&&item.state!=='submitted'?'原因：'+item.error:'');
  putText(view.pending,(item.state==='failed'?'保留内容（未发送）：':item.state==='submitted'?'提交内容：':'待发送：')+(item.sendText||'（尚未生成）'));
  view.copySend.hidden=!item.sendText;
  const position=container.children.item(index);if(position!==view.row)container.insertBefore(view.row,position);
 });
}
async function refreshOutgoing(){
 if(outgoingRefreshing||settingsClosing)return;outgoingRefreshing=true;
 try{const data=await api('outgoing');$('hotkeyState').textContent=data.hotkeyAvailable?'左 Alt＋空格监听已启动 · 仅在游戏聊天输入时响应':'快捷键监听未启动，请确认使用新版后台';drawOutgoing(data.items);}
 catch{$('hotkeyState').textContent='无法获取发送任务，请检查后台连接。';}finally{outgoingRefreshing=false;}
}
function renderDiagnostics(d:any){
 if(!d)return;
 const phase:Record<string,string>={'connected':'游戏 HTTPS 通信正常','https-unavailable':'HTTPS 暂不可用，浏览器设置仍可用','certificate-untrusted':'需要信任本机证书并重启 Steam','game-stale':'曾收到游戏请求，目前心跳中断','bridge-no-reply':'游戏已加载桥接页面，但尚未完成回传','waiting-for-game':'尚未收到游戏请求；进入游戏界面后再检查，刚安装证书需重启 Steam'};
 putText($('diagnosticsSummary'),(phase[d.phase]||d.phase)+' · 后台 '+d.version+' / 游戏脚本 '+(d.clientVersion||'尚未上报'));
 putText($('certificateStatus'),d.certificate.message+(d.certificate.daysRemaining!=null?'（剩余 '+d.certificate.daysRemaining+' 天）':''));
}
bind('checkConnection',async()=>{renderDiagnostics(await api('diagnostics'));notice('连接检查已更新；没有调用翻译 API。');});
bind('copyDiagnostics',async()=>{const d=await api('diagnostics');await copyText(JSON.stringify(d,null,2));notice('诊断已复制，不含聊天正文或 API 密钥。');});

setInterval(()=>{if(!settingsClosing&&(inGame||!document.hidden)){refresh();if(!$('outgoing').hidden)refreshOutgoing();}},1000);
document.querySelector('[data-page="outgoing"]')?.addEventListener('click',refreshOutgoing);

function refreshPreview(){document.querySelectorAll<HTMLElement>('[data-translation-preview]').forEach(label=>label.style.fontSize=$<HTMLSelectElement>('fontSize').value+'px');}
$('fontSize').addEventListener('change',refreshPreview);

if(inGame){document.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();event.stopPropagation();settingsClosing=true;document.title='DLTSETTINGS:close';}});}

function clearChildren(node:HTMLElement){while(node.firstChild)node.removeChild(node.firstChild);}
async function copyText(value:string){
    if(navigator.clipboard&&navigator.clipboard.writeText){try{await navigator.clipboard.writeText(value);return;}catch{}}
    const input=document.createElement('textarea');input.value=value;input.style.position='fixed';input.style.opacity='0';
    const previous=document.activeElement as HTMLElement|null;document.body.appendChild(input);input.focus();input.select();
    try{if(!document.execCommand('copy'))throw new Error('Clipboard unavailable');}finally{input.remove();if(previous&&previous.focus)previous.focus();}
}
function fileText(file:File):Promise<string>{
    if(typeof file.text==='function')return file.text();
    return new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result||''));reader.onerror=()=>reject(new Error('无法读取词库文件'));reader.readAsText(file);});
}

bind('burstPreset',()=>{for(const [id,value] of [['fastConcurrency','4'],['llmConcurrency','4'],['outgoingConcurrency','3'],['outgoingQueueLimit','12']])$<HTMLInputElement>(id).value=value;notice('已填入高频配置：快译 4、精翻 4、发送 3、队列 12。点击保存后生效。');});
