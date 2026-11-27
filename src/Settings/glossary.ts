type Term={source:string,target:string,english:string,kind:string,ambiguous:boolean,legacyTarget?:string,outgoingStandalone?:boolean};

function updateTermsHint(matches?:number){
 const search=$<HTMLInputElement>('termSearch').value.toLowerCase();
 const count=matches??(!search?terms.length:terms.filter(t=>[t.source,t.target,t.legacyTarget||'',t.english].some(value=>value.toLowerCase().includes(search))).length);
 $('termHint').textContent='共 '+terms.length+' 条；匹配 '+count+' 条，最多显示 120 条。搜索可定位其余词条。'+(termsDirty?' · 词库修改尚未保存':'');
}
function setTermSaveStatus(message:string,error=false){
 const status=$('termSaveStatus');status.textContent=message;status.classList.toggle('error',error);
}
const newTermRows=new WeakSet<Term>();
function drawTerms(focusIndex?:number){
 const search=$<HTMLInputElement>('termSearch').value.toLowerCase();const body=$('termRows');clearChildren(body);
 const matches=terms.map((t,index)=>({t,index})).filter(({t})=>[t.source,t.target,t.legacyTarget||'',t.english].some(s=>s.toLowerCase().includes(search)));
 const labels={source:'匹配词',target:'中文译名',legacyTarget:'旧中文（可选）',english:'标准英文名'};
 for(const {t,index} of focusIndex===undefined?matches.slice(0,120):[{t:terms[focusIndex],index:focusIndex},...matches.filter(row=>row.index!==focusIndex).slice(0,119)]){
  const tr=document.createElement('tr');tr.dataset.termIndex=String(index);
  for(const key of ['source','target','legacyTarget','english'] as const){const td=document.createElement('td');const input=document.createElement('input');input.maxLength=key==='source'||key==='english'?100:150;input.value=t[key]||'';input.setAttribute('aria-label',labels[key]);if(key==='legacyTarget')input.placeholder='留空沿用当前名';input.addEventListener('input',()=>t[key]=input.value);td.append(input);tr.append(td);}
  const typeCell=document.createElement('td');const select=document.createElement('select');select.setAttribute('aria-label','类型');
  for(const [v,label] of [['name','专名'],['phrase','短语']]){const o=document.createElement('option');o.value=v;o.textContent=label;select.append(o);}select.value=t.kind;select.addEventListener('change',()=>t.kind=select.value);typeCell.append(select);tr.append(typeCell);
  const td=document.createElement('td');const check=document.createElement('input');check.type='checkbox';check.checked=t.ambiguous;check.setAttribute('aria-label','歧义词');check.addEventListener('change',()=>t.ambiguous=check.checked);td.append(check);tr.append(td);
  const delCell=document.createElement('td');const del=document.createElement('button');del.textContent='×';del.setAttribute('aria-label','删除 '+t.source);
  del.addEventListener('click',async()=>{
   del.disabled=true;
   try{
    await commitNativeEditor();
    const currentIndex=terms.indexOf(t);
    if(currentIndex>=0){terms.splice(currentIndex,1);drawTerms();markTermsDraft();}
   }catch(e){setTermSaveStatus((e as Error).message,true);}
   finally{del.disabled=false;}
  });
  delCell.append(del);tr.append(delCell);body.append(tr);
 }
 updateTermsHint(matches.length);
}
$('termSearch').addEventListener('input',()=>drawTerms());
bind('addTerm',async()=>{
 await commitNativeEditor();
 const row={source:'',target:'',legacyTarget:'',english:'',kind:'name',ambiguous:false};
 newTermRows.add(row);terms.unshift(row);
 $<HTMLInputElement>('termSearch').value='';drawTerms();markTermsDraft();
});
const sameTerm=(a?:Term,b?:Term)=>a===b||!!a&&!!b&&a.source===b.source&&a.target===b.target&&a.english===b.english&&a.kind===b.kind&&a.ambiguous===b.ambiguous&&(a.legacyTarget||'')===(b.legacyTarget||'')&&!!a.outgoingStandalone===!!b.outgoingStandalone;
function mergeTerms(base:Term[],local:Term[],remote:Term[]){
 const index=(list:Term[])=>new Map(list.map(t=>[t.source.toUpperCase(),t]));
 const before=index(base),mine=index(local),theirs=index(remote);
 const keys=new Set([...mine.keys(),...theirs.keys(),...before.keys()]);
 const merged=new Map<string,Term>();
 const conflicts:{key:string;mine?:Term;theirs?:Term}[]=[];
 for(const key of keys){
  const original=before.get(key),a=mine.get(key),b=theirs.get(key);
  const value=sameTerm(a,original)?b:a;
  if(value)merged.set(key,value);
  if(!sameTerm(a,original)&&!sameTerm(b,original)&&!sameTerm(a,b))conflicts.push({key,mine:a,theirs:b});
 }
 return {merged,conflicts};
}
function showTermConflicts(remoteTag:string,remote:Term[]){
 const box=$('termConflicts');clearChildren(box);box.hidden=false;
 const revision=JSON.stringify(terms);
 const {merged,conflicts}=mergeTerms(savedTerms,terms,remote);
 const choices:HTMLSelectElement[]=[];
 for(const item of conflicts){
  const label=document.createElement('label'), select=document.createElement('select');choices.push(select);
  label.textContent=(item.mine||item.theirs)!.source;
  for(const [key,entry] of [['mine',item.mine],['theirs',item.theirs]] as const){
   const option=document.createElement('option');option.value=key;
   option.textContent=(key==='mine'?'本窗口：':'另一窗口：')+(entry?[entry.target,entry.legacyTarget,entry.english,entry.kind,entry.ambiguous?'歧义词':''].filter(Boolean).join(' / '):'删除词条');
   select.append(option);
  }
  label.append(select);box.append(label);
 }
 const apply=document.createElement('button');apply.textContent='采用选择并合并词库';
 apply.onclick=async()=>{
  try{await commitNativeEditor();}catch(e){setTermSaveStatus((e as Error).message,true);return;}
  if(JSON.stringify(terms)!==revision){showTermConflicts(remoteTag,remote);notice('词库草稿已变化，请重新核对冲突。',true);return;}
  conflicts.forEach((item,i)=>{const value=choices[i].value==='mine'?item.mine:item.theirs;if(value)merged.set(item.key,value);else merged.delete(item.key);});
  terms=Array.from(merged.values());savedTerms=remote;termsTag=remoteTag;box.hidden=true;drawTerms();markTermsDraft();setTermSaveStatus('已合并，请点击「保存词库」。');
 };
 box.append(apply);
}
function prepareNewTermAliases(){
 const canonicalNames=new Map(terms.filter(t=>!newTermRows.has(t)&&t.kind==='name'&&t.english&&t.source.toUpperCase()===t.english.toUpperCase())
  .map(t=>[t.english.toUpperCase(),t]));
 const sources=new Set(terms.map(t=>t.source.trim().toUpperCase()));
 const addedAliases:string[]=[];
 for(const row of terms){
  if(!newTermRows.has(row)||row.kind!=='name')continue;
  const source=row.source.trim(),target=row.target.trim(),english=row.english.trim();
  const canonical=canonicalNames.get((english||source).toUpperCase());
  if(!source||!canonical)continue;
  if(source.toUpperCase()===canonical.english.toUpperCase()){
   // The existing name row owns the English match. A new Chinese name on a
   // duplicate draft is a separate alias and retains the original name row.
   const aliasKey=target.toUpperCase();
   if(target.length>100||!/[\u3400-\u9fff]/.test(target)||sources.has(aliasKey)||target===canonical.target)continue;
   row.source=target;row.target=target;row.english=canonical.english;
   sources.add(aliasKey);addedAliases.push(target+' → '+canonical.english);
  }else if(!target){
   row.target=canonical.target;row.english=canonical.english;
   addedAliases.push(source+' → '+canonical.english);
  }
 }
 if(addedAliases.length){drawTerms();markTermsDraft();}
 return addedAliases;
}
function validateTermsDraft(snapshot:Term[]){
 const seen=new Map<string,number>();
 for(let i=0;i<snapshot.length;i++){
  const row=snapshot[i],key=row.source.toUpperCase();
  const issue=!row.source.trim()?'请填写匹配词。':!row.target.trim()?'请填写中文译名。':seen.has(key)?
   '匹配词「'+row.source+'」已存在。添加昵称时，匹配词填你打字用的叫法，例如「尖尖头」，标准英文名填「Yamato」。':'';
  if(issue){
   const first=seen.get(key),index=first!==undefined&&newTermRows.has(terms[first])?first:i;
   focusTerm(index);throw new Error('第 '+(index+1)+' 条：'+issue);
  }
  seen.set(key,i);
 }
}
function focusTerm(index:number){
 let row=document.querySelector<HTMLElement>('#termRows tr[data-term-index="'+index+'"]');
 if(!row&&terms[index]){drawTerms(index);row=document.querySelector<HTMLElement>('#termRows tr[data-term-index="'+index+'"]');}
 if(row){
  if(window.DltSettingsNavigation)window.DltSettingsNavigation.locateElement(row);
  else row.scrollIntoView({block:'center'});
 }
}
bind('saveTerms',async()=>{
 if(!settingsInitialized)return;
 termsSaving=true;$<HTMLButtonElement>('discardTermsDraft').disabled=true;
 try{
  if(settingsComposing)throw new Error('请先完成输入法选词。');
  setTermSaveStatus('正在保存词库…');
  await commitNativeEditor();
  const addedAliases=prepareNewTermAliases();
  for(let attempt=0;attempt<2;attempt++){
   const draftRows=terms.slice(),snapshot=clone(draftRows);
   validateTermsDraft(snapshot);
   try{
    await api('terms','PUT',snapshot);
    draftRows.forEach(row=>newTermRows.delete(row));
    savedTerms=snapshot;termsDirty=JSON.stringify(terms)!==JSON.stringify(snapshot);
    $('termConflicts').hidden=true;updateTermsHint();
    const message=termsDirty?'词库已保存，后续修改仍待保存。':addedAliases.length===1?'词库已保存。'+addedAliases[0]+'。':'词库已保存，发送翻译已使用新词库。';
    setTermSaveStatus(message);notice(message);
    return;
   }catch(e){
    if(!(e instanceof ApiError)||e.status!==409){
     const row=/^第 (\d+) 条/.exec((e as Error).message);
     if(row&&JSON.stringify(terms)===JSON.stringify(snapshot))focusTerm(Number(row[1])-1);
     throw e;
    }
    const latest=await api('terms');
    await commitNativeEditor();
    validateTermsDraft(terms);
    const {merged,conflicts}=mergeTerms(savedTerms,terms,latest.items);
    if(conflicts.length||attempt===1){showTermConflicts(latest.tag,latest.items);throw e;}
    terms=Array.from(merged.values());termsTag=latest.tag;savedTerms=latest.items;drawTerms();markTermsDraft();
   }
  }
 }catch(e){
  const message='词库未保存，当前草稿已保留。'+(e as Error).message;
  setTermSaveStatus(message,true);throw new Error(message);
 }finally{
  termsSaving=false;$<HTMLButtonElement>('discardTermsDraft').disabled=false;
 }
});
bind('exportTerms',()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(terms,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='deadlock-terms.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
bind('importTerms',()=>$<HTMLInputElement>('termFile').click());
$('termFile').addEventListener('change',async()=>{
 const input=$<HTMLInputElement>('termFile'),file=input.files?.[0];if(!file)return;
 try{
  if(file.size>2*1024*1024)throw new Error('词库文件过大');
  const values=JSON.parse(await fileText(file));
  if(!Array.isArray(values)||values.length>5000||values.some(t=>!t||typeof t.source!=='string'||typeof t.target!=='string'||typeof t.english!=='string'||!['name','phrase'].includes(t.kind)||typeof t.ambiguous!=='boolean'||(t.legacyTarget!==undefined&&(typeof t.legacyTarget!=='string'||t.legacyTarget.length>150))||(t.outgoingStandalone!==undefined&&typeof t.outgoingStandalone!=='boolean')))throw new Error('词库格式不正确');
  await commitNativeEditor();
  terms=values;drawTerms();markTermsDraft();notice('已载入词库，点击「保存词库」后生效。');
 }catch(e){setTermSaveStatus((e as Error).message,true);notice((e as Error).message,true);}
 finally{input.value='';}
});

function fileText(file:File):Promise<string>{
    if(typeof file.text==='function')return file.text();
    return new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result||''));reader.onerror=()=>reject(new Error('无法读取词库文件'));reader.readAsText(file);});
}
