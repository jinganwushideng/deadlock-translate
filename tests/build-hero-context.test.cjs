const assert = require('node:assert/strict');
const { fixture, shown } = require('./build-translation-action.test.cjs');

function bindDialog(label, value) {
    let raw=value, bound=true, writes=0;
    Object.defineProperty(label,'text',{get:()=>raw,set:value=>{bound=false;writes++;raw=value;},configurable:true});
    return { update(value){if(bound)raw=value;}, get writes(){return writes;}, get bound(){return bound;} };
}
const f=fixture(false);
Object.assign(f.scene.__dltBuildTextConfig,{backgroundJobs:true,sourceExports:true});
const titleBinding=bindDialog(f.title,'Haze build');
const group=f.category('Core build','Buy from left to right');
const headingBinding=bindDialog(group.first,'Core build');
const noteBinding=bindDialog(group.second,'Buy from left to right');
f.frame(); f.click(); f.frame();
function reply(body,bridge=f.bridge,sent=f.sent){bridge.title='DLTBUILD:'+JSON.stringify({id:sent.at(-1).id,revision:'r1',...body});f.frame(50);}
function source(id,hero,name,bridge=f.bridge,sent=f.sent){
    reply({capture:{source:id.toString(16).padStart(64,'0'),plan:{id,hero,version:1,name}}},bridge,sent);f.frame(110);
    reply({accepted:true},bridge,sent);f.frame(300);
}
source(317831,13,'Haze build');
reply({cursor:3,total:3,done:3,cacheHits:0,translated:3,failed:0,working:false,ended:true,more:false,
    plan:{name:'Haze build',groups:null,fingerprint:'A'.repeat(64)},fields:[
        {source:'Haze build',value:'岚梦出装'},{source:'Core build',value:'核心出装'},{source:'Buy from left to right',value:'从左到右购买'}]});
assert.equal(shown(f.title),'岚梦出装');
assert.equal(f.title.text,'Haze build','the original must retain its native dialog output');
assert.ok(titleBinding.bound&&headingBinding.bound&&noteBinding.bound);
assert.equal(titleBinding.writes+headingBinding.writes+noteBinding.writes,0);

function details(parent,id,name,buildId) {
    const root=f.panel(id,parent);root.paneltype='CitadelBuildDetails';
    const title=f.panel('DltBuildTitle',root,name),note=f.panel('DltBuildNote',root,'Build description');
    f.panel('DltBuildTitleTranslation',root).visible=false;f.panel('DltBuildNoteTranslation',root).visible=false;
    const actions=f.panel('actions',root);f.panel('DltBuildTranslate',actions);
    const bridge=f.panel('DltBuildBridge',root),sent=[];
    bridge.SetURL=url=>{if(url.includes('#'))sent.push(JSON.parse(decodeURIComponent(url.split('#')[1])));};
    const progress=f.panel('DltBuildProgress',root);f.panel('DltBuildProgressFill',progress);f.panel('DltBuildProgressText',progress);
    root.FindChildrenWithClassTraverse=type=>type==='BuildID'?[{text:'Build ID: '+buildId}]:type==='Version'?[{text:'Version: 1'}]:[];
    f.initializePanel(root);
    return {root,title,note,actions,bridge,progress,sent};
}
const popupParent=f.panel('BuildDetailsTooltip',f.scene);
const popup=details(popupParent,'TooltipBuildDetails','Vindicta build',317823);
f.frame();f.frame();
assert.equal(f.scene.__dltBuildText.owner.root,f.root);
assert.equal(popup.progress.visible,false);
assert.equal(popup.actions.visible,false);
assert.equal(popup.sent.length,0,'a hover info window never exports or starts a translation');

// Switching heroes changes native dialog variables on the SAME panel objects.
titleBinding.update('Vindicta build');headingBinding.update('Opening');noteBinding.update('Buy based on lane needs');
f.root.FindChildrenWithClassTraverse=type=>type==='BuildID'?[{text:'Build ID: 317823'}]:type==='Version'?[{text:'Version: 1'}]:[];
f.frame(50);
assert.equal(f.sent.at(-1).build,'Vindicta build');
assert.equal(f.sent.at(-1).buildId,317823);
source(317823,3,'Vindicta build');
reply({cursor:3,total:3,done:3,cacheHits:0,translated:3,failed:0,working:false,ended:true,more:false,
    plan:{name:'Vindicta build',groups:null,fingerprint:'B'.repeat(64)},fields:[
        {source:'Vindicta build',value:'复仇女神出装'},{source:'Opening',value:'前期'},{source:'Buy based on lane needs',value:'按对线情况购买'}]});
f.frame(50);
assert.equal(shown(f.title),'复仇女神出装');
assert.equal(shown(group.first),'前期');assert.equal(shown(group.second),'按对线情况购买');
assert.equal(f.scene.__dltBuildText.owner.root,f.root);
assert.equal(popup.progress.visible,false);
assert.equal(shown(popup.title),'复仇女神出装','matching passive info may read cached text without taking ownership');
assert.equal(titleBinding.writes+headingBinding.writes+noteBinding.writes,0);
console.log('PASS hero dialog updates remain attached and hover info never owns the shop task or progress');

const selector=f.panel('browser',f.scene);selector.paneltype='CitadelHeroBuildsSelector';
const browser=details(selector,'BuildDetails','Browser preview',9001);
const nestedPopupParent=f.panel('nestedPopupParent',selector);nestedPopupParent.classes.add('BuildDetailsTooltip');
const nestedPopup=details(nestedPopupParent,'NestedInfo','Other build',7001);
const embedded=f.panel('ShopModsBuild',browser.root);embedded.paneltype='CitadelShopModsBuild';
const selected=f.panel('SelectedBuildOuter',embedded);f.panel('DltShopTitle',selected,'Browser preview');f.panel('DltShopTitleTranslation',selected);
const embeddedActions=f.panel('actions',embedded);f.panel('DltShopTranslate',embeddedActions);
const eb=f.panel('DltShopBridge',embedded);eb.SetURL=()=>{};
const ep=f.panel('DltShopProgress',embedded);f.panel('DltBuildProgressFill',ep);f.panel('DltBuildProgressText',ep);
f.initializePanel(embedded);f.frame();f.frame();
assert.equal(f.scene.__dltBuildText.owner.root,browser.root,'only actual browser details can own a preview task');
assert.equal(f.scene.__dltBuildText.controls.find(item=>item.root===nestedPopup.root).visible(),false,'tooltip ancestry overrides browser ancestry');
assert.equal(ep.visible,false);assert.equal(embeddedActions.visible,false);
source(9001,3,'Browser preview',browser.bridge,browser.sent);
const previewJob=browser.sent.find(request=>request.action==='start').job;
reply({cursor:1,total:3,done:1,cacheHits:0,translated:1,failed:0,working:true,ended:false,more:false,
    plan:{name:'Browser preview',groups:null,fingerprint:'C'.repeat(64)},fields:[{source:'Browser preview',value:'浏览器预览'}]},browser.bridge,browser.sent);
assert.equal(shown(browser.title),'浏览器预览');
titleBinding.update('Browser preview');headingBinding.update('New build group');noteBinding.update('New build instructions');
f.root.FindChildrenWithClassTraverse=type=>type==='BuildID'?[{text:'Build ID: 9001'}]:type==='Version'?[{text:'Version: 1'}]:[];
selector.visible=false;f.frame();f.frame();
assert.equal(f.scene.__dltBuildText.owner.root,f.root);
reply({capture:{source:(9001).toString(16).padStart(64,'0'),plan:{id:9001,hero:3,version:1,name:'Browser preview'}}});f.frame(110);
assert.equal(f.sent.at(-1).action,'poll');assert.equal(f.sent.at(-1).job,previewJob,'applying a preview resumes its background task');
assert.equal(shown(f.title),'浏览器预览');assert.equal(f.progress.visible,true);assert.equal(popup.progress.visible,false);assert.equal(browser.progress.visible,false);
reply({cursor:3,total:3,done:3,cacheHits:0,translated:3,failed:0,working:false,ended:true,more:false,
    fields:[{source:'New build group',value:'新方案分组'},{source:'New build instructions',value:'新方案说明'}]});
assert.equal(shown(group.first),'新方案分组');assert.equal(shown(group.second),'新方案说明');
console.log('PASS changing hero then applying an uncached browser preview paints the shop and keeps progress on its surface');

const foreign=f.panel('unrelated',f.scene);
f.panel('DltShopBridge',f.panel('nested',foreign));
f.initializePanel(foreign);f.frame();
assert.equal(foreign.__dltBuildTextInitialized,undefined,'descendant IDs cannot classify an unrelated parent as a shop');

const nextScene=f.panel('newHeroScene',null);nextScene.__dltBuildTextConfig={...f.scene.__dltBuildTextConfig};
f.root.paneltype='Panel';f.panel('DltBuildContextShop',f.root);
f.root.parent=nextScene;nextScene.children.push(f.root);
f.frame();f.frame();f.frame();
assert.equal(nextScene.__dltBuildText.owner.root,f.root);
assert.equal(nextScene.__dltBuildText.enabled,true,'manual translation state survives a hero/HUD reparent');
assert.equal(titleBinding.writes+headingBinding.writes+noteBinding.writes,0);
console.log('PASS native role checks reject descendant matches and HUD reparenting binds to the current scene');
