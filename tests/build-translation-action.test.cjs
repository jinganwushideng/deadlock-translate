// Exercise native markup actions, not a synthetic onactivate listener on CitadelButton.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts/dlt_build_text.js'), 'utf8');

function fixture(autoEnabled, withText = true) {
    const tasks = [], sent = [], navigations = [];
    let now = 0;
    function panel(id, parent, text = '') {
        const item = {
            id, text, visible: true, style: {}, children: [],
            IsValid: () => true, GetParent: () => parent,
            FindChildTraverse(id) {
                for (const child of this.children) {
                    if (child.id === id) return child;
                    const found = child.FindChildTraverse(id);
                    if (found) return found;
                }
                return null;
            }
        };
        if (parent) parent.children.push(item);
        return item;
    }
    const scene = panel('scene', null);
    scene.__dltBuildTextConfig = { revision: 'r1', enabled: true, autoEnabled, contextChars: 6000, fastTimeout: 5, qualityTimeout: 30 };
    const root = panel('shop', scene), selected = panel('SelectedBuildOuter', root);
    const title = panel('DltShopTitle', selected, withText ? 'Build title' : '');
    const bridge = panel('DltShopBridge', root), translate = panel('DltShopTranslate', root);
    const original = panel('DltShopOriginal', root), progress = panel('DltShopProgress', root);
    panel('DltBuildProgressFill', progress);
    const status = panel('DltBuildProgressText', progress);
    bridge.SetURL = url => {
        navigations.push(url);
        if (url.includes('#')) sent.push(JSON.parse(decodeURIComponent(url.split('#')[1])));
    };
    const scope = { Date: { now: () => now }, $: {
        GetContextPanel: () => root,
        Schedule: (_, action) => tasks.push(action),
        RegisterEventHandler() {}
    } };
    vm.runInNewContext(source, scope);
    function category(title, note) {
        const group = panel('category', root);
        const first = panel('BuildCategoryName', group, title);
        const second = panel('DltCategoryNote', group, note);
        vm.runInNewContext(source, {
            Date: { now: () => now }, $: {
                GetContextPanel: () => group,
                Schedule: (_, action) => tasks.push(action),
                RegisterEventHandler() {}
            }
        });
        return { first, second };
    }
    const frame = (milliseconds = 150) => {
        now += milliseconds;
        tasks.splice(0).forEach(action => action());
    };
    frame(); frame();
    return { root, title, bridge, progress, status, sent, navigations, tasks, frame, scene, category,
        click: () => scope.DLTBuildTextAction(false),
        showOriginal: () => scope.DLTBuildTextAction(true) };
}

function reply(f, body) {
    f.bridge.title = 'DLTBUILD:' + JSON.stringify({ id: f.sent.at(-1).id, revision: 'r1', ...body });
    f.frame(50);
}
function plan(f, texts = ['Build title']) {
    reply(f, { planStatus: 'ready', plan: { id: 101, hero: 17, version: 1, name: 'Build title', texts } });
}
const automatic = fixture(true);
assert.equal(automatic.sent[0].loadPlan, true);
automatic.click(); automatic.frame();
assert.equal(automatic.sent.length, 1);
plan(automatic);
assert.equal(automatic.sent[1].cacheOnly, true);
reply(automatic, { translations: ['Translated title'], cacheHits: 1 });
assert.equal(automatic.title.text, 'Translated title');
automatic.showOriginal();
assert.equal(automatic.title.text, 'Build title');
assert.equal(automatic.progress.visible, false);
console.log('PASS native action reads local plan first; repeated clicks cannot cancel it; original action restores text');

const empty = fixture(false, false);
empty.click(); empty.frame();
assert.equal(empty.sent.length, 0);
assert.equal(empty.scene.__dltBuildText.collectDelayMs, null);
empty.title.text = 'Loaded title'; empty.frame(50);
assert.equal(empty.sent.length, 1);
assert.equal(empty.sent[0].loadPlan, true);
console.log('PASS loading scheme name prevents unidentified requests');

const identified = fixture(false);
identified.root.FindChildrenWithClassTraverse = () => [{ text: 'Build ID: 4321' }];
identified.click(); identified.frame();
assert.equal(identified.sent[0].buildId, 4321);
identified.root.FindChildrenWithClassTraverse = () => [{ text: 'Build ID: 4322' }];
for(let i=0;i<4;i++)identified.frame();
assert.equal(identified.sent.at(-1).buildId, 4322);
console.log('PASS native ID with localized label prefix disambiguates plans and same-name switches');

const complete = fixture(false);
complete.click(); complete.frame(0);
plan(complete, ['Build title', 'Early group', 'Early note', 'Unseen annotation']);
assert.deepEqual(complete.sent[1].texts, ['Build title', 'Early group', 'Early note', 'Unseen annotation']);
assert.equal(complete.sent[1].context, '');
assert.equal(complete.sent[1].includeSnapshot, false);
assert.match(complete.scene.__dltBuildText.job.context, /Unseen annotation/);
reply(complete, { translations: ['Title result', null, 'Note result', null], cacheHits: 2 });
assert.deepEqual(complete.sent[2].texts, ['Early group', 'Unseen annotation']);
reply(complete, { translations: ['Group result', 'Annotation result'], translated: 2 });
assert.equal(complete.scene.__dltBuildText.job.total, 4);
const later = complete.category('Early group', 'Unseen annotation');
complete.frame(); complete.frame();
assert.equal(later.first.text, 'Group result');
assert.equal(later.second.text, 'Annotation result');
assert.equal(complete.sent.length, 3);
const unrelated = complete.category('Not in plan', 'Other tooltip');
complete.frame(); complete.frame();
assert.equal(unrelated.first.text, 'Not in plan');
assert.equal(complete.sent.length, 3);
complete.showOriginal(); complete.frame(); complete.click(); complete.frame();
assert.equal(complete.sent.at(-1).loadPlan, true);
plan(complete, ['Build title', 'Early group', 'Early note', 'Unseen annotation']);
assert.equal(complete.sent.length, 4);
assert.equal(complete.scene.__dltBuildText.job.cacheHits, 4);
assert.equal(complete.scene.__dltBuildText.job.translated, 0);
console.log('PASS unseen notes translate in the initial job, later UI creation makes no request, reopen uses memo');

const missing = fixture(false);
missing.click(); missing.frame();
reply(missing, { planStatus: 'not-found', plan: null });
missing.frame(); missing.frame();
assert.equal(missing.sent.length, 1);
assert.equal(missing.scene.__dltBuildText.job.failed, 1);
assert.equal(missing.scene.__dltBuildText.job.total, 0);
missing.click(); missing.frame();
assert.equal(missing.sent.length, 2);
assert.equal(missing.sent[1].loadPlan, true);
console.log('PASS missing local source stops cloud consumption and allows explicit retry');

const corrupt = fixture(false);
corrupt.click(); corrupt.frame();
reply(corrupt, { planStatus: 'ready', plan: { name: 'Broken', texts: [42] } });
assert.equal(corrupt.sent.length, 1);
assert.equal(corrupt.scene.__dltBuildText.job.failed, 1);
console.log('PASS malformed local response cannot enter provider queue');

const collection = fixture(false);
for(let i=0;i<30;i++)collection.category('Group '+i,'Description '+i);
collection.frame(); collection.click(); collection.frame(0);
assert.equal(collection.scene.__dltBuildText.collectDelayMs, 0);
assert.equal(collection.scene.__dltBuildText.dispatchDelayMs, 0);
assert.ok(collection.sent[0].loadPlan || collection.sent[0].wire === 1);
const scans=collection.scene.__dltBuildText.collections;
for(let i=0;i<8;i++)collection.frame(50);
assert.equal(collection.scene.__dltBuildText.collections,scans);
console.log('PASS immediate local request and no repeated collection during result polling');
module.exports = { fixture };
