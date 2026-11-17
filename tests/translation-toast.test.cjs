const fs = require('node:fs');
const path = require('node:path');
const Module = require('node:module');
const assert = require('node:assert/strict');

const fixture = path.join(__dirname, 'outgoing-ui.test.cjs');
const loaded = new Module(fixture, module);
loaded.filename = fixture;
loaded.paths = Module._nodeModulePaths(__dirname);
let source = fs.readFileSync(fixture, 'utf8').split('const channelOfTarget =')[0];
source = source.replace('seq: data.seq, serverId: w.serverId, results: [],',
    'seq: data.seq, serverId: w.serverId, results: [],deepSeekNotice:w.notification,');
source = source.replace('Date: { now: () => w.now }', 'Date: class extends Date { static now(){return w.now;} }');
loaded._compile(source + String.fromCharCode(10) + 'module.exports=world;', fixture);
const w = loaded.exports({nativeClasses:true});
w.step(50);
const toast = () => w.root.FindChildTraverse('DltTranslationToast');
const field = id => toast().FindChildTraverse(id);
const charge = (id, category = 'outgoing', cost = {minimum:.0009,maximum:.0009}) =>
    ({id,category,cost,requests:1,missingUsage:cost ? 0 : 1});
w.notification = {items:[charge(1)],balance:[{currency:'CNY',total:2.72}],checkedAt:'2026-11-17T08:00:00Z'};
w.step(25);
assert(toast()?.visible);
assert.equal(field('DltToastTitle').text, '已翻译 1 条');
assert.equal(field('DltToastCostValue').text, '约 ¥0.0009');
assert.equal(field('DltToastBalance').text, '余额 ¥2.72');
assert.match(field('DltToastSubtitle').text, /^DeepSeek/);
assert.equal(toast().hittest, false);
assert.equal(toast().hittestchildren, false);
const panel = toast();
w.notification.items = [charge(1),charge(2)];
w.step(25);
assert.equal(toast(),panel);
assert.equal(field('DltToastTitle').text,'已翻译 2 条');
assert.equal(field('DltToastCostValue').text,'约 ¥0.0018');
w.step(50);
assert.equal(panel.visible, false);
w.step(30);
assert.equal(panel.visible, false);
assert(w.packets.some(p => p.deepSeekNoticeSeen === 2));

w.notification = {items:[charge(3,'build',null)],balance:[],checkedAt:null};
w.step(25);
assert.equal(field('DltToastTitle').text,'出装翻译完成');
assert.equal(field('DltToastCostValue').text,'暂不可估算');
assert.equal(field('DltToastBalance').text,'余额待查询');
w.step(60);
w.notification = {items:[charge(4,'outgoing',{minimum:0,maximum:0})],
    balance:[{currency:'USD',total:1.01},{currency:'CNY',total:.00001}],checkedAt:null};
w.step(25);
assert.equal(field('DltToastCostValue').text,'约 ¥0.00');
assert.equal(field('DltToastBalance').text,'余额 $1.01 / ¥0.00001');
w.step(60);
w.notification = {items:[charge(5,'outgoing',{minimum:.0000002,maximum:.000002})],balance:[]};
w.step(25);
assert.equal(field('DltToastCostValue').text,'约 ¥<0.000001–0.000002');
console.log('PASS compact toast preserves separate cost, balance and counts, groups receipts, suppresses replay, and shows missing/zero/multi-currency values.');
