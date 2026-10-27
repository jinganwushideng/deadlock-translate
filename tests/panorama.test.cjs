const nativeBubble=require('./helpers/native-bubble.cjs'),hudLabel=nativeBubble.translated;
// Receive-path regression. The default scope is all_players: teammates, enemies,
// the local player and player messages whose side is unknown are all translated.
// teammates keeps the old conservative behaviour for comparison.
const vm = require('node:vm'), fs = require('node:fs'), assert = require('node:assert/strict'), { performance } = require('node:perf_hooks');
const source = fs.readFileSync(require('node:path').join(__dirname, '../mod/panorama/scripts/dlt_chat.js'), 'utf8');
let visits = 0, created = 0, now = 100000, urls = [], events = [], tick;
class Panel {
    constructor(type, id, parent, cls = '') {
        this.type = type; this.id = id; this.parent = parent; this.children = []; this.classSet = new Set(cls.split(' '));
        this.text = ''; this.style = {}; this.alive = true;
        if (parent) parent.children.push(this);
    }
    IsValid() { return this.alive; }
    GetParent() { return this.parent; }
    GetChildCount() { visits++; return this.children.length; }
    GetChild(i) { visits++; return this.children[i]; }
    BHasClass(c) { return this.classSet.has(c); }
    AddClass(c) { this.classSet.add(c); }
    FindChildTraverse(id) { visits++; if (this.id === id) return this; for (const child of this.children) { const found = child.FindChildTraverse(id); if (found) return found; } return null; }
    FindChildrenWithClassTraverse(c) { visits++; return this.children.flatMap(p => [...(p.BHasClass(c) ? [p] : []), ...p.FindChildrenWithClassTraverse(c)]); }
    SetURL(url) {
        urls.push(url);
        if (!url.includes('#') && !url.includes('?')) { this.title = 'DLT:ready'; return; }
        const raw = url.split('#')[1] || url.split('?d=')[1];
        const data = JSON.parse(decodeURIComponent(raw));
        events.push(data);
        this.title = 'DLT:' + JSON.stringify({ seq: data.seq, results: [...data.messages.map(m => ({ id: m.id, text: m.contextOnly?'':'译文 ' + m.text, revision: 1, stage: m.contextOnly?'skipped':'fast', done: true }))], config: { enabled: true, fontSize: 17, scanIntervalMs: 200, transport: 'fragment', incomingScope: scope, incomingTargetLanguage:receiveLanguage,outgoingUseContext:useContext,outgoingDisplayMode: 'translation' }, configuredLlm: true });
    }
}
nativeBubble.install(Panel);
const mountNative=host=>nativeBubble.mount((id,parent,cls)=>new Panel('Label',id,parent,cls),host);
const root = new Panel('Root', 'Root'), context = new Panel('CitadelChat', 'Chat', root), messages = new Panel('Panel', 'ChatMessages', context), bridge = new Panel('HTML', 'DltBridge', context), status = new Panel('Label', 'DltStatus', context), hud = new Panel('Panel', 'Team1Chat', root), bubbles = new Panel('Panel', 'Messages', hud);
Panel.prototype.SetParent = function (parent) { if (this.parent) this.parent.children = this.parent.children.filter(c => c !== this); this.parent = parent; parent.children.push(this); };
let scope = 'all_players',receiveLanguage='zh-Hans',useContext=false;
new Panel('TextEntry', 'ChatInput', context); new Panel('Label', 'ChatTargetLabel', context);
const menuParent = new Panel('Panel', 'SubOptions', root); new Panel('Panel', 'DltMenuMount', menuParent);
for (let i = 0; i < 5000; i++) new Panel('Panel', 'unrelated' + i, root);
function row(value, sender = 'teammate', channel = '[ALLIES]', own = false) { const r = new Panel('Panel', '', messages, 'ChatMessage'); if (own) new Panel('Panel', '', r, 'SenderLocalClient'); const body = new Panel('Panel', '', r, 'MessageBody'); const src = new Panel('Panel', 'MessageSource', body); new Panel('Label', '', src, 'ChannelName').text = channel; new Panel('Label', '', src, 'SenderName').text = sender; const contents = new Panel('Panel', 'MessageContents', body), text = new Panel('Panel', '', contents, 'Text'); new Panel('Label', '', text).text = value; return r; }
function bubble(value, parent = bubbles, extra = '') { const r = new Panel('Panel', '', parent, 'ChatMessage ' + extra), contents = new Panel('Panel', 'MessageContents', r), shape = new Panel('Panel', '', contents, 'ChatBubble'), c = new Panel('Panel', '', shape, 'TextContainer'); new Panel('Label', 'MessageText', c).text = value; mountNative(contents); return r; }
const sandbox = { Date: { now: () => now }, Math, JSON, encodeURIComponent, String, Players: { GetLocalPlayer: () => 0, GetTeam: () => 2 }, $: { GetContextPanel: () => context, Schedule: (seconds, fn) => { tick = fn; }, CreatePanel: (type, parent, id) => { created++; return new Panel(type, id, parent); } } };
vm.runInNewContext(source, sandbox);
function step(n = 1) { for (let i = 0; i < n; i++) { now += 200; tick(); } }
function requested(text) { return events.flatMap(e => e.messages).filter(m => m.text === text); }
function queued(text) { return events.flatMap(e => e.messages || []).filter(m => m.text === text); }

for (let i = 0; i < 1000; i++) row('已经翻译过的历史消息', 'system', '[ALL]');
step(30); events = [];
const r = row('wait for my ult'); const b = bubble('wait for my ult'); step(4);
assert.equal(requested('wait for my ult').length, 1, 'one request per message across the two views');
assert.equal(r.FindChildTraverse('DltTranslation').text, '译文 wait for my ult');
assert.equal(hudLabel(b).text, '译文 wait for my ult');
assert.equal(queued('wait for my ult')[0].channel, 'team', 'a team chat line is tagged with the team channel');
const hudOnly = bubble('help on blue lane'); step(4);
assert.equal(requested('help on blue lane').length, 1, 'HUD-only messages independently request translation');
assert.equal(queued('help on blue lane')[0].channel, 'hud', 'HUD bubbles carry no channel and must not mix context');
assert(hudLabel(hudOnly), 'HUD-only translation is attached');

// all_players: enemies, the local player and unknown-side player chat are all translated.
const beforeEnemy = events.flatMap(e => e.messages).length;
const enemyRow = row('enemy message', 'enemy', '[ALL]'); step(3);
assert.equal(events.flatMap(e => e.messages).length, beforeEnemy + 1, 'enemy all-chat is translated by default');
assert(enemyRow.FindChildTraverse('DltTranslation'), 'enemy translation is attached');
assert.equal(queued('enemy message')[0].channel, 'all', 'all-chat uses the all context');
const selfRow = row('my message', 'me', '[ALLIES]', true); step(3);
assert(selfRow.FindChildTraverse('DltTranslation'), 'the local player message is translated by default');
const unknownRow = row('unknown side callout', 'someone', ''); step(3);
assert(unknownRow.FindChildTraverse('DltTranslation'), 'an unknown side must not block a structurally valid player message');
const countWithChinese = events.flatMap(e => e.messages).length;
row('你好队友'); step(3);
assert.equal(events.flatMap(e => e.messages).length, countWithChinese, 'chinese-only text is skipped');
const pingRow = row('ping text', 'teammate', '[ALLIES]'); pingRow.FindChildTraverse('MessageContents').AddClass('Ping'); step(3);
assert.equal(events.flatMap(e => e.messages).length, countWithChinese, 'native ping text is skipped');

// Dedupe must not merge two players or two different views with conflicting sides.
row('wait for my ult', 'another teammate'); step(3);
assert.equal(requested('wait for my ult').length, 2, 'two players saying the same line stay two requests');
row('conflicting side', 'teammate', '[ALLIES]'); bubble('conflicting side', bubbles, 'IsEnemy'); step(4);
assert.equal(requested('conflicting side').length, 2, 'peer in one view and enemy in another are never merged');
const beforeRepeat = events.flatMap(e => e.messages).length; bubble('repeat hud'); bubble('repeat hud'); step(4);
assert.equal(events.flatMap(e => e.messages).length, beforeRepeat + 2, 'identical separate occurrences are not merged');
const label = r.FindChildTraverse('MessageContents').children[0].children[0];
label.text = 'new message'; step(3);
assert.equal(r.FindChildTraverse('DltTranslation').text, '译文 new message', 'an edited row is translated again');
const renderedCount = events.flatMap(e => e.messages).length; step(6);
assert.equal(events.flatMap(e => e.messages).length, renderedCount, 'our own appended translation never re-enters the read path');

// The native snippet loader applies .Text to MessageContents itself, not necessarily to a child.
const native = row('native snippet text'); const nativeContent = native.FindChildTraverse('MessageContents'); nativeContent.AddClass('Text'); nativeContent.children = nativeContent.children[0].children; nativeContent.children[0].parent = nativeContent; step(3);
assert.equal(native.FindChildTraverse('DltTranslation').text, '译文 native snippet text');
const nested = row('nested rich message'); nested.FindChildTraverse('MessageContents').children[0].classSet.delete('Text'); step(4);
assert(nested.FindChildTraverse('DltTranslation'), 'read nested content without requiring a Text class');

// A lobby line keeps its own intake and channel.
const lobby = new Panel('Panel', 'ChatLinesPanel', root), line = new Panel('Panel', '', lobby, 'ChatLineContainer'); new Panel('Label', '', line, 'ChatLinePrefix').text = '[TEAM]'; new Panel('Label', '', line, 'ChatPersona').text = 'lobby teammate'; new Panel('Label', '', line, 'ChatLine').text = 'lobby text'; step(55);
assert(line.FindChildTraverse('DltTranslation'), 'legacy lobby has its own intake');
assert.equal(queued('lobby text')[0].channel, 'team', 'a lobby team prefix maps to the team channel');

// A non-Chinese receive target must process Chinese messages and invalidate old-language views.
const chineseDefault=row('你好队友');step(3);assert.equal(requested('你好队友').length,0);
receiveLanguage='en';step(55);const chineseToEnglish=row('中文消息测试');step(4);
assert.equal(requested('中文消息测试').length,1,'Chinese messages become translatable with English target');
receiveLanguage='ja';step(55);assert.equal(requested('中文消息测试').length,2,'same visible text gets a new language-specific task');
receiveLanguage='zh-Hans';step(55);

// teammates keeps the conservative behaviour and still retries when metadata arrives.
scope = 'teammates'; step(55); events = [];
const filteredEnemy = row('teammates mode enemy', 'enemy', '[ALL]'); step(3);
assert.equal(requested('teammates mode enemy').length, 0, 'teammates mode excludes enemies');
assert.equal(filteredEnemy.FindChildTraverse('DltTranslation'), null);
const filteredSelf = row('teammates mode self', 'me', '[ALLIES]', true); step(3);
assert.equal(requested('teammates mode self').length, 0, 'teammates mode excludes the local player');
row('teammates mode peer'); step(3);
assert.equal(requested('teammates mode peer').length, 1, 'teammates mode still translates teammates');
const delayed = row('late metadata', 'teammate', ''); step(3);
assert.equal(delayed.FindChildTraverse('DltTranslation'), null, 'unknown side stays skipped in teammates mode');
delayed.AddClass('IsFriendly'); step(4);
assert(delayed.FindChildTraverse('DltTranslation'), 'retry when friendship metadata arrives');
scope = 'all_players'; step(55); events = [];
const rescanned = row('scope switched tail'); step(4);
assert(rescanned.FindChildTraverse('DltTranslation'), 'switching back to all_players processes the visible tail');

// Invalid player API results fall back to the native friend marker.
sandbox.Players = { GetLocalPlayer: () => -1, GetTeam: () => -1 }; new Panel('Panel', 'TeamFriendly', root, 'friend team2'); const secondHud = new Panel('Panel', 'Team2Chat', root), secondMessages = new Panel('Panel', 'Messages', secondHud); const sapphire = bubble('sapphire friendly only', secondMessages); step(55);
assert(hudLabel(sapphire), 'invalid player API results fall back to the native friend marker');
const sessionBeforeClear = events.at(-1).session; messages.children.forEach(x => x.alive = false); messages.children = []; step(55);
assert.equal(events.at(-1).session, sessionBeforeClear, 'expiring chat rows must not cancel the match session');

visits = 0; const start = performance.now(); step(10); const elapsed = performance.now() - start; const measuredVisits = visits;
assert(measuredVisits < 20000, 'bounded incremental scan');
sandbox.DLTOpenSettings(); step(3);
assert(events.some(e => e.openSettings), 'the settings bridge request is still delivered');

useContext=true;receiveLanguage='zh-Hans';step(60);
const chineseContext=row('先补装备再打', 'teammate', '[ALLIES]');step(8);
assert.equal(queued('先补装备再打').length,1,'Chinese context captured once');
assert.equal(queued('先补装备再打')[0].contextOnly,true,'Chinese context is not a translation request');
assert.equal(chineseContext.FindChildTraverse('DltTranslation'),null,'context-only row has no translation label');
console.log('PASS Chinese context-only capture, deduplication and no game translation label');

const countBeforeStop = events.length; context.alive = false; step();
assert.equal(events.length, countBeforeStop, 'a dead context stops work');

const report = { passed: 33, normalTenScansMs: elapsed, treeVisits: measuredVisits, unrelatedPanels: 5000, historyRows: 1000, note: 'Node simulation; not game frame-time measurement' };
fs.writeFileSync(require('node:path').join(__dirname, '../artifacts/panorama-tests.json'), JSON.stringify(report, null, 2));
console.log(report);
