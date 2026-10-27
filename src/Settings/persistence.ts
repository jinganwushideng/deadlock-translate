// One conditional config write at a time. New edits remain in DOM while an older write finishes.
let settingsRevision:string|null=null,settingsEpoch=0,savedSettingsEpoch=0;
let saveInFlight:Promise<void>|null=null,autoSaveTimer:ReturnType<typeof setTimeout>|null=null,saveFailed=false;
let savedSettings:Record<string,any>={},lastPublicConfig:any=null,conflictPublic:any=null;
let savedTerms:Term[]=[],termsDirty=false,clipboardInProgress=false,settingsComposing=false;
const clone=<T>(value:T):T=>JSON.parse(JSON.stringify(value));
function saveSuffix(){return nativeBackgroundAvailable?':save:'+(saveFailed?'error':saveInFlight?'saving':nativeEditorSession||settingsEpoch>savedSettingsEpoch?'dirty':'clean'):'';}
function rememberLoadedConfig(data:any){settingsRevision=typeof data.revision==='string'?data.revision:null;savedSettings=clone(current);lastPublicConfig=data;}
function setSaveStatus(text:string,error=false){putText($('autoSaveStatus'),text);$('autoSaveStatus').classList.toggle('error',error);$('retrySave').hidden=!error||!!conflictPublic;$('discardSettingsDraft').hidden=!error;if(settingsInitialized)publishSettingsState('ready');}
function readSettingsDraft(){
 const value:Record<string,any>={...current};
 for(const key of fields){const input=$<HTMLInputElement>(key);if(!input.checkValidity()){input.setAttribute('aria-invalid','true');throw new Error((input.labels?.[0]?.textContent||'设置项').trim().slice(0,60)+'填写不完整或超出范围');}
  value[key]=key==='incomingThinking'||key==='outgoingThinking'?(input.value===''?null:input.value==='true'):input.type==='checkbox'?input.checked:typeof current[key]==='number'?Number(input.value):input.value;
 }
 for(const [key,clear]of[['azureKey',clearAzure],['llmKey',clearLlm]]as const){const input=$<HTMLInputElement>(key);if(clear)value[key]='';else if(input.value.trim())value[key]=input.value.trim();else delete value[key];}
 value.quickMessages=quickMessages.map(q=>({...q}));return value;
}
function nonSecrets(value:Record<string,any>){const copy={...value};delete copy.azureKey;delete copy.llmKey;return copy;}
function changedDraft(value:Record<string,any>){return 'azureKey'in value||'llmKey'in value||JSON.stringify(nonSecrets(value))!==JSON.stringify(nonSecrets(savedSettings));}
function editingSettingsText(){const field=document.activeElement;return !!nativeEditorSession||field instanceof HTMLElement&&field.getClientRects().length>0&&(field instanceof HTMLTextAreaElement||field instanceof HTMLInputElement&&['text','search','password','url','email','tel'].includes(field.type))&&(fields.includes(field.id)||['llmKey','azureKey'].includes(field.id)||!!field.closest('#quickMessageRows'));}
function scheduleAutoSave(){if(autoSaveTimer!==null)clearTimeout(autoSaveTimer);if(!settingsInitialized||settingsClosing||saveFailed||conflictPublic)return;autoSaveTimer=setTimeout(()=>{autoSaveTimer=null;if(!editingSettingsText())void flushSettings(false).catch(()=>{});},500);}
function markSettingsDraft(){if(!settingsInitialized)return;settingsEpoch++;if(!conflictPublic)saveFailed=false;setSaveStatus(conflictPublic?'配置冲突 · 修改已保留':'有修改，等待自动保存',!!conflictPublic);scheduleAutoSave();}
function showConflict(data:any){conflictPublic=data.current;$('saveConflict').hidden=false;const box=$('configConflictFields');clearChildren(box);let local:Record<string,any>;try{local=readSettingsDraft();}catch{return;}
 for(const key of Object.keys(local)){const secret=key==='azureKey'||key==='llmKey';if(!secret&&(JSON.stringify(local[key])===JSON.stringify(savedSettings[key])||JSON.stringify(conflictPublic.settings[key])===JSON.stringify(savedSettings[key])))continue;
  const label=document.createElement('label'),checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.checked=true;checkbox.dataset.configField=key;label.append(checkbox,document.createTextNode((document.getElementById(key)as HTMLInputElement|null)?.labels?.[0]?.childNodes[0]?.textContent?.trim().slice(0,60)||(key==='quickMessages'?'快捷短语':'设置项')));box.append(label);
 }
}
async function flushSettings(force:boolean):Promise<void>{
 if(!settingsInitialized)return;if(autoSaveTimer!==null){clearTimeout(autoSaveTimer);autoSaveTimer=null;}
 if(force){if(settingsComposing)throw new Error('请先完成输入法选词，当前输入仍保留。');await commitNativeEditor();saveFailed=false;}else if(settingsComposing||editingSettingsText()||saveFailed||conflictPublic)return;
 if(conflictPublic)throw new Error('请先处理设置冲突，当前修改已保留。');
 if(saveInFlight){await saveInFlight;if(force&&settingsEpoch>savedSettingsEpoch)return flushSettings(true);return;}
 let value:Record<string,any>;try{value=readSettingsDraft();}catch(e){saveFailed=true;setSaveStatus('填写有误 · 修改已保留',true);notice((e as Error).message,true);throw e;}
 if(!changedDraft(value)){savedSettingsEpoch=settingsEpoch;setSaveStatus('所有更改已保存');return;}
 const epoch=settingsEpoch,keys={azure:$<HTMLInputElement>('azureKey').value,llm:$<HTMLInputElement>('llmKey').value},clear={azure:clearAzure,llm:clearLlm};let success=false;
 saveInFlight=(async()=>{
  setSaveStatus('正在自动保存…');
  try{const data=await api('config','PUT',value);settingsRevision=typeof data.revision==='string'?data.revision:null;lastPublicConfig=data;
   if($<HTMLInputElement>('azureKey').value===keys.azure){$<HTMLInputElement>('azureKey').value='';if(clearAzure===clear.azure)clearAzure=false;}
   if($<HTMLInputElement>('llmKey').value===keys.llm){$<HTMLInputElement>('llmKey').value='';if(clearLlm===clear.llm)clearLlm=false;}
   if(epoch===settingsEpoch)loadFields(data);else{current={...current,...data.settings};savedSettings=clone(current);}
   savedSettingsEpoch=epoch;saveFailed=false;success=true;setSaveStatus(epoch===settingsEpoch?'所有更改已保存':'继续保存后续修改…');
  }catch(e){saveFailed=true;if(e instanceof ApiError&&e.status===409&&e.data?.current)showConflict(e.data);setSaveStatus(conflictPublic?'配置冲突 · 修改已保留':'保存失败 · 修改已保留',true);notice((e as Error).message,true);throw e;}
 })().finally(()=>{saveInFlight=null;if(success&&settingsEpoch>savedSettingsEpoch)scheduleAutoSave();if(settingsInitialized)publishSettingsState('ready');});
 return saveInFlight;
}
function save(){return flushSettings(true);}
bind('retrySave',save);
bind('discardSettingsDraft',async()=>{if(saveInFlight)try{await saveInFlight;}catch{}await cancelNativeEditor();if(!lastPublicConfig)return;clearAzure=clearLlm=false;$<HTMLInputElement>('azureKey').value='';$<HTMLInputElement>('llmKey').value='';loadFields(lastPublicConfig);savedSettingsEpoch=++settingsEpoch;saveFailed=false;conflictPublic=null;$('saveConflict').hidden=true;setSaveStatus('已撤销未保存修改');});
bind('resolveSettingsConflict',async()=>{
 const local=readSettingsDraft(),patch:Record<string,any>={};for(const key of Object.keys(local))if(JSON.stringify(local[key])!==JSON.stringify(savedSettings[key]))patch[key]=local[key];
 document.querySelectorAll<HTMLInputElement>('[data-config-field]').forEach(box=>{if(!box.checked)delete patch[box.dataset.configField!];});
 const remote=await api('config');clearAzure=patch.azureKey==='';clearLlm=patch.llmKey==='';$<HTMLInputElement>('azureKey').value=typeof patch.azureKey==='string'?patch.azureKey:'';$<HTMLInputElement>('llmKey').value=typeof patch.llmKey==='string'?patch.llmKey:'';
 loadFields({...remote,settings:{...remote.settings,...patch}});lastPublicConfig=remote;savedSettings=clone(remote.settings);conflictPublic=null;$('saveConflict').hidden=true;saveFailed=false;settingsEpoch++;await save();
});
bind('useRemoteSettings',async()=>{const remote=await api('config');await cancelNativeEditor();clearAzure=clearLlm=false;$<HTMLInputElement>('azureKey').value='';$<HTMLInputElement>('llmKey').value='';loadFields(remote);savedSettingsEpoch=++settingsEpoch;saveFailed=false;conflictPublic=null;$('saveConflict').hidden=true;setSaveStatus('已采用另一窗口的设置');});
function markTermsDraft(){termsDirty=true;const hint=$('termHint');if(!hint.textContent?.includes('尚未保存'))hint.textContent+=' · 词库修改尚未保存';}
bind('discardTermsDraft',async()=>{await cancelNativeEditor();terms=clone(savedTerms);termsDirty=false;drawTerms();notice('词库已恢复到上次保存的内容。');});
let leavingSettings:Promise<void>|null=null;
function leaveSettings(reload=false):Promise<void>{
 if(settingsReloadRequested||settingsClosing)return Promise.resolve();
 if(leavingSettings)return leavingSettings;
 leavingSettings=finishLeavingSettings(reload).finally(()=>{leavingSettings=null;});return leavingSettings;
}
async function finishLeavingSettings(reload:boolean){
 try{await save();if(termsDirty){notice('词库还有未保存的修改，请保存词库或撤销词库修改后再关闭。',true);await selectPage('glossary');return;}
  await stopRecording();await stopDisplayPreview();if(reload){settingsReloadRequested=true;window.DltSettingsNavigation?.cancel();if(inGame){$('autoSaveStatus').textContent='正在重新加载…';document.title='DLTSETTINGS:reload:'+(++settingsSignal)+backgroundSuffix()+saveSuffix()+windowLoadSuffix();}else location.reload();return;}
  settingsClosing=true;window.DltSettingsNavigation?.cancel();if(inGame)document.title='DLTSETTINGS:close'+windowLoadSuffix();else{document.querySelector<HTMLElement>('.shell')!.hidden=true;document.body.insertAdjacentHTML('beforeend','<div class="closed-settings"><h2>设置已关闭</h2><p>更改已保存在本机，可重新打开设置。</p><button id="reopenSettings">重新打开</button></div>');$('reopenSettings').onclick=()=>location.reload();}
 }catch(e){notice((e as Error).message,true);}
}
bind('closeSettings',()=>leaveSettings());
$('reloadSettings').addEventListener('click',async()=>{
 const button=$<HTMLButtonElement>('reloadSettings');if(settingsReloadRequested||button.disabled)return;button.disabled=true;
 try{await leaveSettings(true);}finally{button.disabled=settingsReloadRequested;}
});
for(const event of ['input','change'])document.addEventListener(event,e=>{const target=e.target as HTMLElement|null;if(!target||clipboardInProgress)return;if(target.closest('#termRows')){markTermsDraft();return;}if(fields.includes(target.id)||['llmKey','azureKey'].includes(target.id)||target.closest('#quickMessageRows')){target.removeAttribute('aria-invalid');markSettingsDraft();}});
document.addEventListener('focusout',()=>scheduleAutoSave());
document.addEventListener('compositionstart',()=>{settingsComposing=true;});
document.addEventListener('compositionend',()=>{settingsComposing=false;scheduleAutoSave();});
window.addEventListener('beforeunload',e=>{if(settingsEpoch>savedSettingsEpoch||termsDirty||nativeEditorSession){e.preventDefault();e.returnValue='';}});
