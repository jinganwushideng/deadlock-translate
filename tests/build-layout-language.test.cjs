const assert=require('node:assert/strict');
const {fixture,shown}=require('./build-translation-action.test.cjs');

const chinese=fixture(true,true,'中文出装攻略 [tracklock.gg]');
assert.equal(chinese.sent.length,0,'a Chinese scheme does not export or start automatically');
assert.equal(chinese.translate.text,'翻译当前出装');
chinese.click();chinese.frame();
assert.equal(chinese.sent.length,1,'Chinese schemes retain an explicit manual action');
assert.equal(chinese.sent[0].automatic,false);
chinese.title.text='Foreign build';chinese.frame(50);
assert.equal(chinese.sent.at(-1).build,'Foreign build');
assert.equal(chinese.sent.at(-1).automatic,true,'switching to foreign content resumes the enabled automatic workflow');
console.log('PASS Chinese plans remain manual while foreign plans continue automatically');

const body=fixture(true);
Object.assign(body.scene.__dltBuildTextConfig,{backgroundJobs:true,sourceExports:true});
body.title.text='International guide';body.frame(50);
function reply(value){body.bridge.title='DLTBUILD:'+JSON.stringify({id:body.sent.at(-1).id,revision:'r1',...value});body.frame(50);}
reply({capture:{source:'A'.repeat(64),plan:{id:101,hero:17,version:1,name:'International guide',isChinese:true}}});
body.frame(150);
assert.ok(!body.sent.some(x=>x.action==='start'),'a complete Chinese body is stopped before a provider job');
assert.equal(body.translate.text,'翻译当前出装');
body.click();body.frame(50);
assert.equal(body.sent.at(-1).action,'start');assert.equal(body.sent.at(-1).automatic,false);
console.log('PASS full-source language detection protects Chinese guides with foreign titles and allows manual override');

const layout=fixture(false);
const parent=layout.title.GetParent();parent.actualuiscale_x=1.5;parent.actualuiscale_y=2;parent.GetPositionWithinWindow=()=>({x:30,y:80});
layout.title.GetPositionWithinWindow=()=>({x:222,y:440});layout.title.actuallayoutwidth=300;layout.title.actuallayoutheight=48;
const output=parent.FindChildTraverse('DltShopTitleTranslation');
const before={width:layout.title.actuallayoutwidth,height:layout.title.actuallayoutheight,text:layout.title.text};
assert.ok(layout.positionTranslation(layout.title,output));
assert.equal(output.style.position,'128px 180px 0px');assert.equal(output.style.width,'200px');assert.equal(output.style.height,'24px');
assert.deepEqual({width:layout.title.actuallayoutwidth,height:layout.title.actuallayoutheight,text:layout.title.text},before,'drawing a translation never resizes or rewrites the native label');
console.log('PASS overlay coordinates respect UI scaling and leave native layout metrics unchanged');

const opacity=fixture(false);
opacity.title.style.opacity='0.7';opacity.click();opacity.frame();
function opacityReply(value){opacity.bridge.title='DLTBUILD:'+JSON.stringify({id:opacity.sent.at(-1).id,revision:'r1',...value});opacity.frame(50);}
opacityReply({planStatus:'ready',plan:{name:'Build title',id:101,hero:17,version:1,texts:['Build title'],groups:[],fingerprint:'B'.repeat(64)}});
opacityReply({translations:['中文标题'],cacheHits:1});
assert.equal(opacity.title.style.opacity,'0','inline hiding overrides native selector opacity');
opacity.click();opacity.frame();assert.equal(opacity.title.style.opacity,'0.7','original mode restores the native opacity');
console.log('PASS source hiding wins native CSS specificity and restores its original appearance');
