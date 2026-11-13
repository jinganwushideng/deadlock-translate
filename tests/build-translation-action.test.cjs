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
    const frame = () => {
        now += 150;
        tasks.splice(0).forEach(action => action());
    };
    frame(); frame();
    return { root, title, bridge, progress, status, sent, navigations, tasks, frame, scene, category,
        click: () => scope.DLTBuildTextAction(false),
        showOriginal: () => scope.DLTBuildTextAction(true) };
}

const automatic = fixture(true);
assert.equal(automatic.sent.length, 1);
automatic.click();
assert.equal(automatic.scene.__dltBuildText.enabled, true);
assert.equal(automatic.progress.visible, true);
assert.match(automatic.status.text, /读取本地译文/);
automatic.frame();
assert.equal(automatic.sent.length, 1);
assert.match(automatic.status.text, /0\/1/);
automatic.bridge.title = 'DLTBUILD:' + JSON.stringify({
    id: automatic.sent[0].id, revision: 'r1', translations: ['译文']
});
automatic.frame();
assert.equal(automatic.title.text, '译文');
automatic.showOriginal();
assert.equal(automatic.title.text, 'Build title');
assert.equal(automatic.progress.visible, false);
automatic.frame();
assert.equal(automatic.scene.__dltBuildText.enabled, false);
console.log('PASS repeated clicks keep an active request; original action restores the source');

const manual = fixture(false);
assert.equal(manual.sent.length, 0);
manual.click();
assert.equal(manual.progress.visible, true);
manual.frame();
assert.equal(manual.sent.length, 1);
console.log('PASS manual native action starts translation and immediately reveals status');

const empty = fixture(false, false);
empty.click();
empty.frame();
assert.equal(empty.sent.length, 0);
assert.equal(empty.progress.visible, true);
assert.match(empty.status.text, /未读到/);
console.log('PASS missing text is visible as a status instead of silent inactivity');

const recover = fixture(false);
let throwOnce = true;
Object.defineProperty(recover.title, 'text', {
    get() {
        if (throwOnce) { throwOnce = false; throw Error('transient panel'); }
        return 'Recovered title';
    },
    set() {}
});
recover.click();
recover.frame();
assert.equal(recover.scene.__dltBuildText.error, true);
assert.equal(recover.tasks.length, 1);
recover.frame();
assert.equal(recover.scene.__dltBuildText.error, false);
assert.equal(recover.sent.length, 1);
console.log('PASS a transient panel error keeps the update loop alive and retries');

const cached = fixture(false);
const group = cached.category('Early group', 'Early note');
cached.frame();
cached.click(); cached.frame();
assert.deepEqual(cached.sent[0].texts, ['Build title', 'Early group', 'Early note']);
assert.equal(cached.sent[0].cacheOnly, true);
cached.bridge.title = 'DLTBUILD:' + JSON.stringify({
    id: cached.sent[0].id, revision: 'r1', translations: ['标题', null, '说明'], cacheHits: 2
});
cached.frame();
assert.deepEqual(cached.sent[1].texts, ['Early group']);
assert.equal(cached.sent[1].cacheOnly, false);
cached.bridge.title = 'DLTBUILD:' + JSON.stringify({
    id: cached.sent[1].id, revision: 'r1', translations: ['分组'], cacheHits: 0, translated: 1
});
cached.frame();
assert.equal(group.first.text, '分组');
assert.match(cached.status.text, /本地 2 · 新译 1/);
cached.showOriginal(); cached.frame(); cached.click(); cached.frame();
assert.equal(cached.sent.length, 2);
assert.match(cached.status.text, /本地 3 · 新译 0/);
cached.scene.__dltBuildText.readers.reverse();
cached.frame();
assert.equal(cached.sent.length, 2);
group.first.text = 'Changed group';
cached.frame();
assert.deepEqual(cached.sent[2].texts, ['Changed group']);
assert.equal(cached.sent[2].cacheOnly, true);
console.log('PASS cached fields never go to translation; reopen and traversal reorder reuse results; changed fields alone are checked');
