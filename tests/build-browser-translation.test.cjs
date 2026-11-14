const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
let now = 0, queue = [], rows = [], sent = [];
function panel(id, parent = null, text = '') {
    const p = { id, text, visible: true, style: {}, children: [], IsValid: () => true,
        GetParent: () => parent, BHasClass: () => false,
        FindChildTraverse(id) { for (const c of this.children) { if (c.id === id) return c; const found = c.FindChildTraverse(id); if (found) return found; } return null; } };
    if (parent) parent.children.push(p);
    return p;
}
const scene = panel('scene'), root = panel('selector', scene), list = panel('HeroBuildList', root);
const button = panel('DltListTranslate', root), bridge = panel('DltListBridge', root);
const status = panel('DltListStatus', root), search = panel('SearchTextEntry', root);
scene.__dltBuildTextConfig = { enabled: true, revision: 'r1' };
list.FindChildrenWithClassTraverse = () => rows;
bridge.SetURL = url => { if (url.includes('#')) sent.push(JSON.parse(decodeURIComponent(url.split('#')[1]))); };
function row(id, title) { const p = panel(id, list); panel('DltListName', p, title); panel('DltListNameTranslation', p); return p; }
rows = [row('row1', 'Plan A'), row('row2', 'Plan B')];
const scope = { Date: { now: () => now }, $: { GetContextPanel: () => root, Schedule: (_, fn) => queue.push(fn) } };
vm.runInNewContext(['dlt_build_transport.js', 'dlt_build_browser.js'].map(name => fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts', name), 'utf8')).join('\n'), scope);
function frame(ms = 150) { now += ms; queue.splice(0).forEach(fn => fn()); }
function reply(body) { bridge.title = 'DLTBUILD:' + JSON.stringify({ id: sent.at(-1).id, revision: 'r1', ...body }); frame(); }
frame(); frame();
assert.equal(sent.length, 0, 'searching never translates without a click');
root.__dltBuildListAction(); frame();
assert.deepEqual(sent[0].texts, ['Plan A', 'Plan B']);
assert.equal(sent[0].action, 'list');
reply({ accepted: true }); frame(350);
reply({ fields: [{ source: 'Plan A', value: '方案甲' }, { source: 'Plan B', value: '方案乙' }], cursor: 2, done: 2, total: 2, ended: true, more: false });
assert.equal(rows[0].FindChildTraverse('DltListNameTranslation').text, '方案甲');
assert.equal(rows[0].FindChildTraverse('DltListName').text, 'Plan A', 'native search source remains untouched');
rows = [row('row1', 'Plan A'), row('row2', 'Plan B')]; frame();
assert.equal(rows[0].FindChildTraverse('DltListNameTranslation').text, '方案甲', 'recycled rows bind without another request');
const before = sent.length;
search.text = 'new'; rows = [row('row3', 'Plan C')]; frame(); frame();
assert.equal(sent.length, before, 'new search result requires its own explicit click');
assert.equal(rows[0].FindChildTraverse('DltListNameTranslation').text, '');
root.__dltBuildListAction(); frame();
assert.deepEqual(sent.at(-1).texts, ['Plan C']);
reply({ fields: [{ source: 'Plan A', value: '旧方案' }], cursor: 1, ended: true, more: false });
assert.equal(rows[0].FindChildTraverse('DltListNameTranslation').text, '', 'old fields cannot bind to a new title');
console.log('PASS browser list translation is manual, preserves search source, rebinds recycled rows and isolates changed results');
