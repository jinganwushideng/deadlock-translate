// Safety checks for the removed paint workaround; historic pulse tests remain in Git.
const assert=require('node:assert/strict'),fs=require('node:fs'),world=require('./helpers/chat-world.cjs');
const w=world();let calls=0;w.Panel.prototype.ApplyStyles=()=>{calls++;throw Error('paint workaround removed');};const b=w.bubble('native paint retired');w.step(14);assert.equal(b.__dltLabel.text,'译文 native paint retired');assert.equal(calls,0);console.log('PASS native translation needs no forced style application');
const css=fs.readFileSync('mod/panorama/styles/dlt_native_bubble.css','utf8');
assert(!css.includes('animation-')&&!css.includes('@keyframes'),'no animation on native rows or labels');
assert(!css.includes('DltNativeRowRepaint'),'no row repaint class');
console.log('PASS no native animation, keyframes or row repaint class');
const source=fs.readFileSync('mod/panorama/scripts/dlt_chat.js','utf8');assert(!source.includes('DltNativePaintA')&&!source.includes('SetReadyForDisplay(')&&!source.includes('ROOT.ApplyStyles(')&&!source.includes('host.style.height ='));console.log('PASS no forced readiness or parent size mutation');
const controller=fs.readFileSync('mod/panorama/scripts/dlt_native_bubble.js','utf8');assert(!controller.includes('Repaint')&&!controller.includes('brightness'));console.log('PASS no row animation is added by the native controller');
console.log(JSON.stringify({passed:4,failed:0,realGame:false,legacyPaintRetired:true,rowAnimationUntouched:true}));