// Exercise native markup actions, not a synthetic onactivate listener on CitadelButton.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts/dlt_build_text.js'), 'utf8');

function fixture(autoEnabled, withText = true) {
    const tasks = [], sent = [], navigations = [], dispatched = [];
    let now = 0;
    function panel(id, parent, text = '') {
        const item = {
            id, text, parent, paneltype: 'Panel', visible: true, style: {}, children: [], classes: new Set(),
            IsValid: () => true, GetParent: () => item.parent,
            GetChildCount: () => item.children.length, GetChild: index => item.children[index],
            SetHasClass(name, value) { if(value)this.classes.add(name);else this.classes.delete(name); },
            BHasClass(name) { return this.classes.has(name); },
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
    root.paneltype = 'CitadelShopModsBuild';
    const title = panel('DltShopTitle', selected, withText ? 'Build title' : '');
    panel('DltShopTitleTranslation', selected).visible = false;
    const bridge = panel('DltShopBridge', root), actions = panel('actions',root), translate = panel('DltShopTranslate', actions);
    const progress = panel('DltShopProgress', root);
    panel('DltBuildProgressFill', progress);
    const status = panel('DltBuildProgressText', progress);
    bridge.SetURL = url => {
        navigations.push(url);
        if (url.includes('#')) sent.push(JSON.parse(decodeURIComponent(url.split('#')[1])));
    };
    const scope = { Date: { now: () => now }, $: {
        GetContextPanel: () => root,
        Schedule: (_, action) => tasks.push(action),
        DispatchEvent: (...args) => dispatched.push(args),
        RegisterEventHandler() {}
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts/dlt_build_transport.js'), 'utf8') + '\n' + source, scope);
    function initializePanel(group) {
        vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts/dlt_build_transport.js'), 'utf8') + '\n' + source, {
            Date: { now: () => now }, $: {
                GetContextPanel: () => group,
                Schedule: (_, action) => tasks.push(action),
                RegisterEventHandler() {}
            }
        });
    }
    function category(title, note) {
        const group = panel('category', root);
        group.paneltype = 'CitadelShopModsBuildCategory';
        const first = panel('BuildCategoryName', group, title);
        const second = panel('DltCategoryNote', group, note);
        panel('DltCategoryTitleTranslation',group).visible = false;
        panel('DltCategoryNoteTranslation',group).visible = false;
        initializePanel(group);
        return { first, second };
    }
    function addTooltip() {
        const popup = panel('tooltip', scene);
        popup.paneltype = 'CitadelTooltipModDetails';
        panel('ModName', popup, 'Equipment A');
        panel('DltBuildTitleTranslation', popup);
        const note = panel('DltBuildNote', popup, 'First annotation');
        const output = panel('DltBuildNoteTranslation', popup);
        initializePanel(popup);
        return { popup, note, output };
    }
    const frame = (milliseconds = 150) => {
        now += milliseconds;
        tasks.splice(0).forEach(action => action());
    };
    frame(); frame();
    return { root, title, bridge, translate, progress, status, sent, navigations, dispatched, tasks, frame, scene, category, addTooltip, panel, initializePanel,
        click: () => scope.DLTBuildTextAction(),
        showOriginal: () => scope.DLTBuildTextAction() };
}
function shown(label) {
    const id={DltShopTitle:'DltShopTitleTranslation',BuildCategoryName:'DltCategoryTitleTranslation',DltCategoryNote:'DltCategoryNoteTranslation',DltBuildTitle:'DltBuildTitleTranslation',DltBuildNote:'DltBuildNoteTranslation'}[label.id];
    const output=id&&label.GetParent()?.FindChildTraverse(id);
    return output?.visible&&output.style.visibility!=='collapse'?output.text:label.text;
}

function reply(f, body) {
    f.bridge.title = 'DLTBUILD:' + JSON.stringify({ id: f.sent.at(-1).id, revision: 'r1', ...body });
    f.frame(50);
}
function plan(f, texts = ['Build title'], fields = {}) {
    reply(f, { planStatus: 'ready', plan: { id: 101, hero: 17, version: 1, name: 'Build title',
        texts, fingerprint: 'A'.repeat(64), groups: [], ...fields } });
}
const automatic = fixture(true);
assert.equal(automatic.sent[0].loadPlan, true);
automatic.click(); automatic.frame();
assert.equal(automatic.sent.length, 1);
assert.equal(automatic.scene.__dltBuildText.enabled, false);
assert.equal(automatic.translate.text, '翻译当前出装');
assert.equal(automatic.progress.visible, false);
assert.equal(automatic.navigations.at(-1), 'about:blank');
automatic.click(); automatic.frame();
assert.equal(automatic.sent.length, 2);
assert.equal(automatic.translate.text, '显示原文');
plan(automatic);
assert.equal(automatic.sent[2].cacheOnly, true);
reply(automatic, { translations: ['Translated title'], cacheHits: 1 });
assert.equal(shown(automatic.title), 'Translated title');
automatic.showOriginal();
assert.equal(shown(automatic.title), 'Build title');
assert.equal(automatic.progress.visible, false);
assert.equal(automatic.translate.text, '翻译当前出装');
assert.equal(automatic.root.FindChildTraverse('DltShopOriginal'), null);
console.log('PASS one toggle starts translation, cancels pending work, restores original text and changes its caption');

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
const savedGroup = { name: 'Early group', note: 'Unseen annotation' };
plan(complete, ['Build title', 'Early group', 'Early note', 'Unseen annotation'], { groups: [savedGroup] });
assert.deepEqual(complete.sent[1].texts, ['Build title', 'Early group', 'Early note', 'Unseen annotation']);
assert.equal(complete.sent[1].context, '');
assert.equal(complete.sent[1].includeSnapshot, false);
assert.match(complete.scene.__dltBuildText.job.context, /Unseen annotation/);
reply(complete, { translations: ['Title result', null, 'Note result', null], cacheHits: 2 });
assert.deepEqual(complete.sent[2].texts, ['Early group', 'Unseen annotation']);
reply(complete, { translations: ['Group result', 'Annotation result'], translated: 2 });
assert.equal(complete.scene.__dltBuildText.job.total, 4);
const later = complete.category('Early group', 'Unseen   annotation');
complete.frame(); complete.frame();
assert.equal(shown(later.first), 'Group result');
assert.equal(shown(later.second), 'Annotation result');
assert.equal(complete.sent.length, 3);
complete.showOriginal(); complete.frame(); complete.click(); complete.frame();
assert.equal(complete.sent.at(-1).loadPlan, true);
plan(complete, ['Build title', 'Early group', 'Early note', 'Unseen annotation'], { groups: [savedGroup] });
assert.equal(complete.sent.length, 4);
assert.equal(complete.scene.__dltBuildText.job.cacheHits, 4);
assert.equal(complete.scene.__dltBuildText.job.translated, 0);
console.log('PASS unseen notes translate in the initial job, later UI creation makes no request, manual off/on uses memo');

const reopened = fixture(false);
const reusedGroup = reopened.category('Early group', 'Unseen annotation');
reopened.frame(); reopened.click(); reopened.frame();
assert.equal(reopened.progress.visible, false, 'a local plan lookup does not show progress');
plan(reopened, ['Build title', 'Early group', 'Unseen annotation'], { groups: [savedGroup] });
assert.equal(reopened.progress.visible, false, 'a local cache lookup does not show progress');
reply(reopened, { translations: ['Translated title', 'Translated group', null], cacheHits: 2 });
assert.equal(reopened.progress.visible, true, 'an actual cache miss shows translation progress');
reply(reopened, { translations: ['Translated note'], translated: 1 });
assert.equal(reopened.progress.visible, true, 'new translation completion is visible');
const previousRequests = reopened.sent.length;
reopened.root.visible = false;
reopened.frame();
reopened.root.visible = true;
reopened.frame();
assert.equal(shown(reopened.title), 'Translated title');
assert.equal(shown(reusedGroup.first), 'Translated group');
assert.equal(shown(reusedGroup.second), 'Translated note');
assert.equal(reopened.sent.length, previousRequests, 'an identical full scheme needs no bridge request on reopen');
assert.equal(reopened.progress.visible, false, 'completion toast is not replayed');
reopened.root.visible = false;
reopened.frame();
reusedGroup.second.text = 'Changed instructions';
reopened.root.visible = true;
reopened.frame();
assert.equal(reopened.sent.length, previousRequests + 1, 'same title with updated groups must be checked again');
assert.equal(reopened.sent.at(-1).loadPlan, true);
assert.equal(shown(reusedGroup.second), 'Changed instructions');
assert.equal(reopened.progress.visible, false);
console.log('PASS reopening one scheme restores results silently; reopened matching title with edited instructions starts a new lookup');

const background = fixture(false);
background.scene.__dltBuildTextConfig.backgroundJobs = true;
background.category('Core', 'Build note');
background.frame(); background.click(); background.frame();
const start = background.sent.at(-1);
assert.equal(start.action, 'start');
assert.match(start.job, /^[a-f0-9]{32}$/);
reply(background, { accepted: true });
background.root.visible = false;
background.frame(500);
assert.equal(background.sent.length, 1, 'closing a shop does not request cancellation of background work');
background.root.visible = true;
background.frame();
const poll = background.sent.at(-1);
assert.equal(poll.action, 'poll');
assert.equal(poll.job, start.job, 'reopening attaches to the same backend job');
reply(background, { cursor: 3, total: 3, done: 3, cacheHits: 0, translated: 3, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', fingerprint: 'C'.repeat(64), groups: [{ name: 'Core', note: 'Build note' }] },
    fields: [{ source: 'Build title', value: 'Translated build' }, { source: 'First annotation', value: 'First translation' }, { source: 'Second annotation', value: 'Second translation' }] });
assert.equal(shown(background.title), 'Translated build');
assert.equal(background.progress.visible, false, 'a job completed while away does not replay the progress popup');
const popup = background.addTooltip();
background.frame(40); background.frame(40);
assert.equal(popup.output.text, 'First translation');
popup.note.text = 'Second annotation';
background.frame(40);
assert.equal(popup.output.text, 'Second translation', 'one tooltip tick repaints the recycled panel');
popup.note.text = 'New tooltip text';
background.frame(40);
assert.equal(popup.output.visible, false, 'an unmatched annotation clears the previous one immediately');
background.frame(220);
const noteStart = background.sent.at(-1);
assert.equal(noteStart.action, 'note');
assert.equal(noteStart.parentJob, start.job);
assert.deepEqual(noteStart.texts, ['New tooltip text']);
reply(background, { accepted: true });
background.frame(300);
assert.equal(background.sent.at(-1).action, 'poll');
reply(background, { cursor: 1, total: 1, done: 1, cacheHits: 0, translated: 1, failed: 0, working: false, ended: true, more: false,
    fields: [{ source: 'New tooltip text', value: 'New note translation' }] });
assert.equal(popup.output.text, 'New note translation');
console.log('PASS background job survives shop close, recycled tooltip refreshes in one tick, and a missing annotation is queued separately');

const swapped = fixture(false);
const group = swapped.category('Shared title', 'Original instructions');
swapped.frame(); swapped.click(); swapped.frame();
plan(swapped, ['Build title', 'Shared title', 'Original instructions'], { groups: [{ name: 'Shared title', note: 'Original instructions' }] });
const oldLookup = swapped.sent.at(-1).id;
group.second.text = 'Different instructions';
swapped.frame(500);
assert.equal(swapped.sent.at(-1).loadPlan, true);
assert.deepEqual(swapped.sent.at(-1).groups, [{ name: 'Shared title', note: 'Different instructions' }]);
swapped.bridge.title = 'DLTBUILD:' + JSON.stringify({ id: oldLookup, revision: 'r1', translations: ['Old build', 'Old group', 'Old note'] });
swapped.frame(50);
assert.equal(shown(group.first), 'Shared title');
assert.equal(shown(group.second), 'Different instructions');
reply(swapped, { planStatus: 'ambiguous', fallback: 'B'.repeat(64) });
assert.equal(swapped.sent.at(-1).cacheOnly, true);
assert.deepEqual(swapped.sent.at(-1).texts, ['Build title', 'Shared title', 'Different instructions']);
reply(swapped, { translations: ['New build', null, null], cacheHits: 1 });
assert.equal(shown(swapped.title), 'New build');
assert.equal(shown(group.first), 'Shared title');
reply(swapped, { translations: ['New group', 'New note'], translated: 2 });
assert.equal(shown(group.first), 'New group');
assert.equal(shown(group.second), 'New note');
assert.equal(swapped.translate.text, '显示原文');
console.log('PASS same-title replacement disowns late results, translates the current panel only, and updates without a toggle');

const missing = fixture(false);
missing.click(); missing.frame();
reply(missing, { planStatus: 'not-found', plan: null });
missing.frame(); missing.frame();
assert.equal(missing.sent.length, 1);
assert.equal(missing.scene.__dltBuildText.job.failed, 1);
assert.equal(missing.scene.__dltBuildText.job.total, 0);
missing.click(); missing.frame();
assert.equal(missing.scene.__dltBuildText.enabled, false);
assert.equal(missing.sent.length, 1);
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
const exported = fixture(false);
Object.assign(exported.scene.__dltBuildTextConfig, { backgroundJobs: true, sourceExports: true });
exported.click(); exported.frame();
assert.equal(exported.sent.at(-1).action, 'source-prepare');
reply(exported, { capture: { token: 'a'.repeat(32), manual: false } });
assert.deepEqual(exported.dispatched, [['CitadelCopySelectedBuildContentsToClipboard', 0]]);
exported.frame(110);
assert.equal(exported.sent.at(-1).action, 'source-read');
reply(exported, { capture: { pending: true } });
assert.equal(exported.scene.__dltBuildText.job.failed, 0, 'native clipboard latency is pending, not a failed export');
exported.frame(110);
reply(exported, { capture: { source: 'B'.repeat(64) } });
exported.frame(110);
assert.equal(exported.sent.at(-1).action, 'start');
assert.equal(exported.sent.at(-1).source, 'B'.repeat(64));
reply(exported, { accepted: true });
exported.frame(300);
reply(exported, { cursor: 2, total: 2, done: 2, cacheHits: 0, translated: 2, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', fingerprint: 'B'.repeat(64), groups: [], visibleOnly: false },
    fields: [{ source: 'Build title', value: '整套出装' }, { source: 'First annotation', value: '预先翻译的注释', fieldId: 'groups/0/items/0/note' }] });
const exportedPopup = exported.addTooltip(); exported.frame(40); exported.frame(40);
assert.equal(exportedPopup.output.text, '预先翻译的注释');
const requestsAfterExport = exported.sent.length;
exportedPopup.note.text = 'Other item'; exported.frame(300); exported.frame(300);
assert.equal(exported.sent.length, requestsAfterExport, 'hovering cannot trigger additional cloud translation');
exported.click(); exported.frame();
assert.ok(!exported.sent.some(request => request.action === 'cancel'), 'showing originals keeps backend work alive');
console.log('PASS complete source handshake tolerates pending export, binds unseen annotations and never translates on hover');

const manualExport = fixture(false);
Object.assign(manualExport.scene.__dltBuildTextConfig, { backgroundJobs: true, sourceExports: true });
manualExport.click(); manualExport.frame();
reply(manualExport, { capture: { token: 'c'.repeat(32), manual: true } });
assert.equal(manualExport.dispatched.length, 0, 'unsafe clipboard formats cannot trigger automatic overwrite');
assert.equal(manualExport.progress.visible, true);
assert.match(manualExport.status.text, /复制到剪贴板/);
console.log('PASS unsupported clipboard contents expose manual export without overwriting clipboard');
module.exports = { fixture, shown };
