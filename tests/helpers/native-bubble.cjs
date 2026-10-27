const vm=require('node:vm');
function install(Panel){
 Panel.prototype.SetAttributeString??=function(k,v){this.nativeAttrs??={};this.nativeAttrs[k]=String(v);};
 Panel.prototype.GetAttributeString??=function(k,f){return this.nativeAttrs?.[k]??f;};
 Panel.prototype.SetAttributeInt??=function(k,v){this.nativeAttrs??={};this.nativeAttrs[k]=Number(v);};
 Panel.prototype.GetAttributeInt??=function(k,f){return this.nativeAttrs?.[k]??f;};
 Panel.prototype.RunScriptInPanelContext=function(script){
  this.nativeContextCalls=(this.nativeContextCalls||0)+1;this.lastNativeScript=script;this.inNativeContext=true;
  try{vm.runInNewContext(script,{$:{GetContextPanel:()=>this}});}finally{this.inNativeContext=false;}
 };

 Panel.prototype.SetDialogVariable=function(key,value){this.variables??={};this.variables[key]=String(value);};
 Panel.prototype.RemoveClass=function(c){(this.cls||this.classSet)?.delete(c);};
 Panel.prototype.BReadyForDisplay=function(){return this.readyForDisplay??true;};
 Panel.prototype.BIsTransparent=function(){return this.fullyTransparent??(Number(this.style.opacity)===0);};
 Panel.prototype.ApplyStyles=function(){};
 Panel.prototype.ClearPropertyFromCode=function(key){delete this.style[key];(this.clearedFromCode??=[]).push(key);};
}
// Mirrors the shipped bubble stylesheet: a translation label is visible and
// zero-sized until it gets text, and it stays in the engine layout while idle.
function prepare(label){
 label.html=false;label.hittest=false;label.visible=true;label.style.opacity='0';label.style.color='#28241e';label.style.visibility='visible';label.style.width='fit-children';
 label.actuallayoutwidth=0;label.actuallayoutheight=0;label.nativeCreated=true;
 let value='';Object.defineProperty(label,'text',{configurable:true,get(){return value;},set(raw){value=String(raw);this.nativeTextWrites=(this.nativeTextWrites||0)+1;if(this.style.visibility==='collapse'){this.actuallayoutwidth=0;this.actuallayoutheight=0;}else{this.actuallayoutwidth=value?Math.min(220,value.length*10):0;this.actuallayoutheight=value?24:0;}if(this.onNativeTextWrite)this.onNativeTextWrite(value);}});
 return label;
}
// The native snippet label is part of the row from creation, so the engine has
// already assigned layout offsets to it before any translation exists.
function mount(create,host){const label=prepare(create('DltBubbleTranslation',host,'DltBubbleTranslation DltNativeBubbleTemplate'));label.actualxoffset=0;label.actualyoffset=0;label.nativePrelaidOut=true;return label;}
// Simulates the real-world stall: the native row keeps the label at 0x0.
function mountStalled(create,host){const label=mount(create,host);label.onNativeTextWrite=()=>{label.actuallayoutwidth=0;label.actuallayoutheight=0;};return label;}
function translated(row){
 const label=row.FindChildTraverse('DltBubbleTranslation');
 return label&&label.IsValid()&&label.visible!==false&&Number(label.style.opacity)>0&&label.__dltNativeValue?label:null;
}
module.exports={install,mount,mountStalled,translated,prepare};
