const assert = require('node:assert/strict');
const { fixture } = require('./build-translation-action.test.cjs');

function sourceFixture() {
    const f = fixture(false);
    Object.assign(f.scene.__dltBuildTextConfig, { backgroundJobs: true, sourceExports: true });
    f.reply = body => {
        f.bridge.title = 'DLTBUILD:' + JSON.stringify({ id: f.sent.at(-1).id, revision: 'r1', ...body });
        f.frame(50);
    };
    f.capture = (id, version = 1, source = id.toString(16).padStart(64, '0')) => {
        f.reply({ capture: { source, plan: { id, hero: 17, version, name: f.title.__dltBuildOriginal?.source || f.title.text, groups: [] } } });
        f.frame(110);
    };
    f.accept = () => { f.reply({ accepted: true }); f.frame(300); };
    f.identify = (id, version) => {
        f.root.FindChildrenWithClassTraverse = name => name === 'BuildID' ? [{ text: 'Build ID: ' + id }] : name === 'Version' ? [{ text: 'Version: ' + version }] : [];
    };
    return f;
}

const draining = sourceFixture();
draining.click(); draining.frame(); draining.capture(101); draining.accept();
assert.equal(draining.sent.find(x => x.action === 'source-prepare').texts.length, 0);
assert.equal(draining.sent.find(x => x.action === 'start').texts.length, 0, 'a source job starts by its verified snapshot instead of uploading visible text again');
draining.reply({ cursor: 1, total: 3, done: 3, cacheHits: 0, translated: 3, failed: 0, working: false, ended: true, more: true, fieldCount: 3,
    plan: { name: 'Build title', groups: [], fingerprint: 'A'.repeat(64) }, fields: [{ source: 'Build title', value: '出装甲' }] });
assert.equal(draining.progress.visible, true, 'backend completion cannot hide progress while result pages remain');
assert.match(draining.status.text, /接收译文/);
draining.reply({ cursor: 3, total: 3, done: 3, cacheHits: 0, translated: 3, failed: 0, working: false, ended: true, more: false, fieldCount: 3,
    fields: [{ source: 'Late heading', value: '后期标题' }, { source: 'Late instructions', value: '后期说明' }] });
const late = draining.category('Late heading', 'Late instructions');
draining.frame(50); draining.frame(50);
assert.equal(late.first.text, '后期标题');
assert.equal(late.second.text, '后期说明');
console.log('PASS result draining remains visible and late fields paint from the completed source');

const versions = sourceFixture();
versions.identify(101, 1); versions.click(); versions.frame(); versions.capture(101); versions.accept();
versions.reply({ cursor: 1, total: 1, done: 1, cacheHits: 1, translated: 0, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', groups: [], fingerprint: 'A'.repeat(64) }, fields: [{ source: 'Build title', value: '版本一' }] });
versions.identify(101, 2); versions.frame(50);
assert.equal(versions.sent.at(-1).action, 'source-prepare', 'same ID with a new version must revalidate the source');
assert.equal(versions.sent.at(-1).buildVersion, 2);
console.log('PASS version changes are detected without waiting for title or groups to change');

const recycled = sourceFixture();
recycled.identify(102, 1);
const group = recycled.category('Header', 'Instructions');
recycled.frame(); recycled.click(); recycled.frame(); recycled.capture(102); recycled.accept();
recycled.reply({ cursor: 3, total: 3, done: 3, cacheHits: 0, translated: 3, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', groups: [{ name: 'Header', note: 'Instructions' }], fingerprint: 'B'.repeat(64) },
    fields: [{ source: 'Build title', value: '标题' }, { source: 'Header', value: '分组' }, { source: 'Instructions', value: '说明' }] });
const host = group.first.GetParent();
host.children = [];
group.first.IsValid = group.second.IsValid = () => false;
const newHeader = recycled.panel('BuildCategoryName', host, 'Header');
const newNote = recycled.panel('DltCategoryNote', host, 'Instructions');
const requestsBeforeRebind = recycled.sent.length;
recycled.frame(50);
assert.equal(newHeader.text, '分组', 'a recreated native label must bind to its existing translation');
assert.equal(newNote.text, '说明');
assert.equal(recycled.sent.length, requestsBeforeRebind, 'recreated fields in a verified native version need no source reload');
console.log('PASS recycled native labels rebind and translate in the next UI tick');

const revisited = sourceFixture();
revisited.click(); revisited.frame(); revisited.capture(201); revisited.accept();
revisited.reply({ cursor: 2, total: 2, done: 2, cacheHits: 0, translated: 2, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', groups: [], fingerprint: 'C'.repeat(64) }, fields: [{ source: 'Build title', value: '出装甲' }, { source: 'First annotation', value: '甲注释' }] });
revisited.title.text = 'Build B'; revisited.frame(50);
revisited.capture(202); revisited.accept();
revisited.reply({ cursor: 1, total: 1, done: 1, cacheHits: 0, translated: 1, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build B', groups: [], fingerprint: 'D'.repeat(64) }, fields: [{ source: 'Build B', value: '出装乙' }] });
revisited.title.text = 'Build title'; revisited.frame(50);
const startsBeforeReturn = revisited.sent.filter(x => x.action === 'start').length;
revisited.capture(201);
assert.equal(revisited.title.text, '出装甲');
assert.equal(revisited.sent.filter(x => x.action === 'start').length, startsBeforeReturn, 'verified native source reuse does not restart or redownload the completed job');
const note = revisited.addTooltip(); revisited.frame(40); revisited.frame(40);
assert.equal(note.output.text, '甲注释');
assert.equal(revisited.progress.visible, false, 'a completed cached scheme does not replay the popup');
console.log('PASS A-B-A switching restores the exact native source and all cached fields without another job');

const waiting = sourceFixture();
waiting.click(); waiting.frame(); waiting.frame(600);
assert.equal(waiting.progress.visible, true, 'a slow source read has a visible loading state');
waiting.reply({ capture: { pending: true } }); waiting.frame(150);
assert.equal(waiting.progress.visible, true, 'waiting for an export cannot drop a shown progress popup');
waiting.capture(301); waiting.accept();
waiting.reply({ cursor: 0, total: 4, done: 0, cacheHits: 0, translated: 0, failed: 0, working: false, ended: false, more: false, fields: [] });
assert.equal(waiting.progress.visible, true, 'the cache/queue phase remains visible before cloud work starts');
console.log('PASS loading and queue transitions keep progress visible until the UI has completed');

const shared = sourceFixture();
const sharedGroup = shared.category('Shared header', 'Instructions A');
shared.frame(); shared.click(); shared.frame(); shared.capture(401); shared.accept();
shared.reply({ cursor: 2, total: 2, done: 2, cacheHits: 0, translated: 2, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', groups: [{ name: 'Shared header', note: 'Instructions A' }], fingerprint: 'E'.repeat(64) },
    fields: [{ source: 'Shared header', value: '甲方案标题' }, { source: 'Instructions A', value: '甲方案说明' }] });
sharedGroup.second.text = 'Instructions B'; shared.frame(50);
shared.capture(402);
assert.equal(shared.scene.__dltBuildText.job.done, 0);
assert.equal(sharedGroup.first.text, 'Shared header', 'a different native source cannot inherit a shared heading from the previous scheme');
assert.equal(shared.sent.at(-1).action, 'start');
console.log('PASS same titles and shared headings cannot restore fields from a different native source');

const caption = sourceFixture();
caption.title.text = 'Build title [60% WR]'; caption.identify(501, 1);
caption.click(); caption.frame(); caption.capture(501); caption.accept();
caption.reply({ cursor: 1, total: 1, done: 1, cacheHits: 0, translated: 1, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title [60% WR]', groups: [], fingerprint: 'F'.repeat(64) }, fields: [{ source: 'Build title [60% WR]', value: '方案 [60% 胜率]' }] });
caption.title.text = 'Build title [61% WR]'; caption.frame(50);
assert.equal(caption.sent.at(-1).action, 'source-prepare');
caption.capture(501);
assert.equal(caption.scene.__dltBuildText.job.done, 0, 'changed title numbers cannot restore a previous caption under the same native version');
console.log('PASS changed caption numbers are revalidated under the same native ID and version');

const contentChanged = sourceFixture();
const changedGroup = contentChanged.category('Header', 'Old content');
contentChanged.frame(); contentChanged.click(); contentChanged.frame(); contentChanged.capture(601); contentChanged.accept();
contentChanged.reply({ cursor: 1, total: 1, done: 1, cacheHits: 0, translated: 1, failed: 0, working: false, ended: true, more: false,
    plan: { name: 'Build title', groups: [{ name: 'Header', note: 'Old content' }], fingerprint: '6'.repeat(64) }, fields: [{ source: 'Header', value: '旧标题' }] });
changedGroup.second.text = 'New content'; contentChanged.frame(50);
contentChanged.capture(601, 1, '9'.repeat(64));
assert.equal(contentChanged.scene.__dltBuildText.job.done, 0, 'a changed complete source cannot reuse fields solely from matching native metadata');
console.log('PASS complete source fingerprints distinguish changed content even when native metadata is unchanged');
