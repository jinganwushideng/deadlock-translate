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
    const frame = () => {
        now += 150;
        tasks.splice(0).forEach(action => action());
    };
    frame(); frame();
    return { root, title, bridge, progress, status, sent, navigations, tasks, frame, scene,
        click: () => scope.DLTBuildTextAction(false),
        showOriginal: () => scope.DLTBuildTextAction(true) };
}

const automatic = fixture(true);
assert.equal(automatic.sent.length, 1);
automatic.click();
assert.equal(automatic.scene.__dltBuildText.enabled, true);
assert.equal(automatic.progress.visible, true);
assert.equal(automatic.status.text, '正在读取出装');
automatic.frame();
assert.equal(automatic.sent.length, 2);
assert.match(automatic.status.text, /0\/1/);
automatic.bridge.title = 'DLTBUILD:' + JSON.stringify({
    id: automatic.sent[1].id, revision: 'r1', translations: ['译文']
});
automatic.frame();
assert.equal(automatic.title.text, '译文');
automatic.showOriginal();
assert.equal(automatic.title.text, 'Build title');
assert.equal(automatic.progress.visible, false);
automatic.frame();
assert.equal(automatic.scene.__dltBuildText.enabled, false);
console.log('PASS automatic mode translate button still starts work; original action restores the source');

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
