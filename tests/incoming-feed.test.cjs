const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const world = require('./helpers/chat-world.cjs');
const source = fs.readFileSync(process.env.DLT_CHAT_TEST_SOURCE || path.join(__dirname, '../mod/panorama/scripts/dlt_chat.js'), 'utf8');
const exposed = source.replace("audit('adapter-start');", "ROOT.__dltFixtureState=state; audit('adapter-start');");
let passed = 0, failed = 0;
function check(name, fn) { try { fn(); passed++; console.log('PASS ' + name); } catch (e) { failed++; console.error('FAIL ' + name + ': ' + e.stack); } }
function fresh(mode = 'both', own = true) {
    const w = world(exposed); Object.assign(w.config, { incomingDisplayMode: mode, incomingShowOwnTranslations: own, incomingFeedSeconds: 8, incomingFeedLines: 4 }); w.refresh(); return w;
}
const state = w => w.root.__dltFixtureState, feed = w => w.root.FindChildTraverse('DltIncomingFeed');
const active = w => (feed(w)?.children || []).filter(p => p.visible && p.style.visibility !== 'collapse');
const events = w => [...new Map(w.packets.flatMap(p => p.logs || []).map(e => [e.seq, e])).values()];
function localEcho(w, text = 'hello team', original = '你好', channel = 'team') {
    state(w).recentOwn.push({ text, original, channel, displayMode: 'translation', at: w.now, views: {} });
    const b = w.bubble(text); b.AddClass('IsSelf'); return b;
}
check('local outgoing echo enters the left feed without another cloud request', () => {
    const w = fresh(), b = localEcho(w); w.step(8);
    assert.equal(b.__dltRecord.hudSeen, true); assert.equal(active(w).length, 1); assert.equal(active(w)[0].children[1].text, '你好');
    assert(active(w)[0].children[0].text.includes('友军 · 原文回显')); assert.equal(w.requests.length, 0); assert.equal(state(w).metrics.localEchoReused, 1);
});
check('independent-only mode also receives local all-chat echo', () => {
    const w = fresh('independent'); localEcho(w, 'hello all', '大家好', 'all'); w.step(8);
    assert.equal(active(w).length, 1); assert(active(w)[0].children[0].text.includes('全体')); assert.equal(w.requests.length, 0);
});
check('disabling own translation display suppresses the local echo feed and native addition', () => {
    const w = fresh('both', false), b = localEcho(w); w.step(8);
    assert.equal(active(w).length, 0); assert(!b.__dltLabel?.text); assert.equal(b.FindChildTraverse('MessageText').text, 'hello team'); assert.equal(w.requests.length, 0);
});
check('one switch hides own history, bubble and feed without changing others or requests', () => {
    const w = fresh(), r = w.row('my own foreign line'), b = w.bubble('my own foreign line'); r.AddClass('IsSelf'); b.AddClass('IsSelf');
    const other = w.bubble('peer foreign line'); w.step(8); assert.equal(active(w).length, 2); const requests = w.requests.length;
    w.config.incomingShowOwnTranslations = false; w.refresh(); w.step(2);
    assert.equal(r.__dltLabel.text, ''); assert.equal(r.__dltLabel.visible, false); assert(!b.__dltLabel?.text);
    assert.equal(active(w).length, 1); assert(other.__dltLabel.text); assert.equal(w.requests.length, requests);
    assert.equal(b.FindChildTraverse('MessageText').text, 'my own foreign line');
});
check('reenabling own display restores live cached results without a new translation', () => {
    const w = fresh(), r = w.row('cached own'), b = w.bubble('cached own'); r.AddClass('IsSelf'); b.AddClass('IsSelf'); w.step(7);
    const requests = w.requests.length, slot = state(w).feedSlots.find(s => s.record === b.__dltRecord), until = slot.until;
    w.config.incomingShowOwnTranslations = false; w.refresh(); w.config.incomingShowOwnTranslations = true; w.refresh(); w.step(1);
    assert(b.__dltLabel.text); assert(r.__dltLabel.text); assert.equal(r.__dltLabel.visible, true); assert.equal(active(w).length, 1);
    assert.equal(slot.until, until); assert.equal(w.requests.length, requests);
});
check('delayed own results stay hidden while other players continue to display', () => {
    const w = fresh('both', false); w.ready = () => w.now + 1600;
    const b = w.bubble('delayed own'); b.AddClass('IsSelf'); w.bubble('delayed peer'); w.step(20);
    assert(!b.__dltLabel?.text); assert.equal(active(w).length, 1); assert(active(w)[0].children[1].text.includes('delayed peer'));
});
check('history self marker is authoritative without guessing from an account name', () => {
    const w = fresh('both', false), own = w.row('marked local'); new w.Panel('', own, 'SenderLocalClient'); const peer = w.row('unmarked peer'); w.step(8);
    assert(!own.__dltLabel?.text); assert(peer.__dltLabel?.text);
});
check('hidden card title and translated text recover in place', () => {
    const w = fresh(); w.bubble('recover card'); w.step(8); const slot = state(w).feedSlots.find(s => s.record), until = slot.until, requests = w.requests.length;
    slot.panel.visible = false; slot.panel.style.visibility = 'collapse'; slot.title.visible = false; slot.text.style.opacity = '0'; w.step(4);
    assert(slot.panel.visible && slot.panel.style.visibility === 'visible'); assert(slot.title.visible); assert.equal(slot.text.style.opacity, '1');
    assert.equal(slot.until, until); assert.equal(w.requests.length, requests); assert(events(w).some(e => e.code === 'feed-restored'));
});
check('hidden owned feed root restores configured opacity without touching the game root', () => {
    const w = fresh(); w.config.incomingOpacity = 55; w.refresh(); w.bubble('recover root'); w.step(8); const f = feed(w); f.visible = false; f.style.visibility = 'collapse'; f.style.opacity = '0'; w.step(4);
    assert.equal(feed(w), f); assert(f.visible); assert.equal(f.style.visibility, 'visible'); assert.equal(f.style.opacity, '0.55'); assert.equal(w.root.style.visibility, undefined);
});
check('a still-valid detached card is rebuilt with its original deadline', () => {
    const w = fresh(); w.bubble('reparented card'); w.step(8); const slot = state(w).feedSlots.find(s => s.record), old = slot.panel, until = slot.until;
    const foreign = new w.Panel('ForeignNativePanel', w.root); old.parent.children = old.parent.children.filter(p => p !== old); old.parent = foreign; foreign.children.push(old); w.step(4);
    assert.equal(old.visible, false); assert.equal(foreign.IsValid(), true); assert.equal(active(w).length, 1); assert.equal(state(w).feedSlots.find(s => s.record).until, until); assert.equal(w.requests.length, 1);
});
check('expired hidden cards are not revived or extended by recovery', () => {
    const w = fresh(); w.config.incomingFeedSeconds = 3; w.refresh(); w.bubble('terminal feed'); w.step(8); const f = feed(w); f.style.visibility = 'collapse'; w.step(20);
    assert.equal(active(w).length, 0); w.step(10); assert.equal(active(w).length, 0); assert.equal(w.requests.length, 1);
});
check('hidden own backlog is not replayed when its switch is enabled later', () => {
    const w = fresh('both', false); localEcho(w); w.step(8); w.config.incomingShowOwnTranslations = true; w.refresh(); w.step(4); assert.equal(active(w).length, 0);
});
check('ineffective visibility setters are bounded and not logged as repaired', () => {
    const w = fresh(); w.bubble('failing setter'); w.step(8); const slot = state(w).feedSlots.find(s => s.record); let attempts = 0;
    Object.defineProperty(slot.panel, 'visible', { get: () => false, set: () => { attempts++; } }); const before = events(w).filter(e => e.code === 'feed-restored').length; w.step(10);
    assert(attempts <= 2); assert.equal(events(w).filter(e => e.code === 'feed-restored').length, before);
});
check('delayed feed samples record zero geometry without claiming pixels are visible', () => {
    const w = fresh(); w.bubble('PRIVATE_FEED_SOURCE'); w.step(4); const slot = state(w).feedSlots.find(s => s.record); slot.text.actuallayoutwidth = 0; slot.text.actuallayoutheight = 0; w.step(9);
    const samples = events(w).filter(e => e.code === 'feed-layout'); assert(samples.length > 0 && samples.length <= 2); assert(samples.some(e => (e.feed.flags & 256) !== 0));
    assert(!JSON.stringify(events(w)).includes('PRIVATE_FEED_SOURCE'));
});
check('scene transitions discard delayed independent-feed samples and old content', () => {
    const w = fresh(); w.bubble('old sample'); w.step(5); w.match++; w.step(15); assert.equal(active(w).length, 0); assert.equal(state(w).feedChecks.length, 0);
});
const result = { passed, failed, realGame: false, cloudUsed: false, pixelsVerified: false };
fs.writeFileSync(path.join(__dirname, '../artifacts/incoming-feed-tests.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result)); if (failed) process.exitCode = 1;
