const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const makeWorld = require('./helpers/chat-world.cjs');
const source = fs.readFileSync(process.env.DLT_LOCAL_CONTROLLER_SOURCE || path.join(__dirname, '../mod/panorama/scripts/dlt_native_bubble.js'), 'utf8');
const gateClass = 'DltNativeRelayoutGate';
let passed = 0;
function check(name, test) { test(); passed++; console.log('PASS ' + name); }
function fixture({ zero = true, styleThrows = false, scheduleThrows = false, dropShort = false, initialClass = '', hasStyles = true, stopAtNudge = false, missingClear = false } = {}) {
    const w = makeWorld(), queue = [], events = [], styleWrites = [];
    w.stallNative = zero;
    w.hud.actuallayoutwidth = 900; w.hud.actuallayoutheight = 700;
    w.hud.style = new Proxy(w.hud.style, { set(target, key, value) { styleWrites.push([String(key), String(value)]); target[key] = value; return true; } });
    let serial = 0;
    const add = w.hud.AddClass.bind(w.hud), remove = w.hud.RemoveClass.bind(w.hud);
    w.hud.AddClass = name => { events.push({ name, kind: 'add', at: w.now }); add(name); };
    w.hud.RemoveClass = name => { events.push({ name, kind: 'remove', at: w.now }); remove(name); };
    w.hud.ApplyStyles = () => { events.push({ kind: 'apply', at: w.now }); if (styleThrows) throw Error('style failure'); };
    if (!hasStyles) w.hud.ApplyStyles = undefined;
    if (missingClear) w.hud.ClearPropertyFromCode = undefined;
    if (initialClass) w.hud.AddClass(initialClass);
    function boot() {
        vm.runInNewContext(source, { Date: { now: () => w.now }, $: {
            GetContextPanel: () => w.hud,
            Schedule: (delay, fn) => {
                assert([.2, .03, .05].includes(delay));
                if (delay === .03 && scheduleThrows) throw Error('schedule failure');
                const id = ++serial;
                if (!(delay === .03 && dropShort)) queue.push({ at: w.now + delay * 1000, fn, id });
                return id;
            },
            CancelScheduled: id => { const i = queue.findIndex(t => t.id === id); if (i >= 0) queue.splice(i, 1); },
            RegisterEventHandler: () => {},
            CreatePanel: (_, parent, id) => {
                const p = require('./helpers/native-bubble.cjs').prepare(new w.Panel(id, parent));
                if (zero) { p.onNativeTextWrite = () => { p.actuallayoutwidth = p.actuallayoutheight = 0; }; }
                return p;
            }
        }});
    }
    function advance(ms = 200) {
        w.step(1, ms);
        const due = queue.filter(t => t.at <= w.now);
        for (const task of due) { queue.splice(queue.indexOf(task), 1); task.fn(); }
    }
    boot();
    const b = w.bubble('root layout check');
    for (let i = 0; i < 20 && !w.hud.BHasClass(gateClass); i++) { advance(); if(stopAtNudge&&styleWrites.length)break; }
    return { w, b, advance, boot, queue, events, styleWrites, gates: () => events.filter(e => e.kind === 'add' && e.name === gateClass) };
}
check('zero-sized output gates only the native chat root across separate callbacks', () => {
    const f = fixture(), label = f.b.__dltLabel;
    assert.equal(f.gates().length, 1); assert(f.w.hud.BHasClass(gateClass));
    assert(!f.w.root.BHasClass(gateClass)); assert(!f.w.chatContainer.BHasClass(gateClass));
    const at = f.w.now; f.advance(20); assert(f.w.hud.BHasClass(gateClass));
    f.advance(15); assert(!f.w.hud.BHasClass(gateClass)); assert(f.w.now > at);
    assert.equal(Number(label.GetAttributeString('dlt_native_reflow_flags', '0')), 15);
    assert.equal(label.actuallayoutwidth, 0); // The mock does NOT claim a GPU/layout repair.
});
check('a stalled message first resizes the chat root without hiding anything', () => {
    const f = fixture();
    const sizeWrites = f.styleWrites.filter(([key]) => key === 'width' || key === 'height');
    assert(sizeWrites.length >= 2);
    assert(sizeWrites.some(([key, value]) => key === 'width' && value === '899px'));
    assert(sizeWrites.some(([key, value]) => key === 'height' && value === '699px'));
    assert.deepEqual(f.w.hud.clearedFromCode, ['width', 'height']);
    assert.equal(f.w.hud.style.width, undefined);
    assert.equal(f.w.hud.style.visibility, undefined);
});check('positive-sized translations never use the collapse path', () => { const f = fixture({ zero: false }); assert.equal(f.gates().length, 0); });
check('an already-open native scoreboard/detail view is never toggled', () => {
    for (const name of ['gScoreboardOpen', 'gDetailView']) {
        const f = fixture({ initialClass: name }); assert.equal(f.gates().length, 0); assert(f.w.hud.BHasClass(name));
    }
});
check('a stable zero-size failure is attempted once per message', () => { const f = fixture(); for (let i = 0; i < 30; i++) f.advance(); assert.equal(f.gates().length, 1); });
check('bursts share root cycles with at least 800ms between cycles', () => {
    const f = fixture(); for (let i = 0; i < 8; i++) f.w.bubble('root burst ' + i);
    for (let i = 0; i < 50; i++) f.advance();
    const gates = f.gates(); assert(gates.length >= 2 && gates.length <= 9);
    for (let i = 1; i < gates.length; i++) assert(gates[i].at - gates[i - 1].at >= 800);
});
check('normal scoreboard state is preserved if Tab opens during restoration', () => {
    const f = fixture(); f.w.hud.AddClass('gScoreboardOpen'); f.advance(35);
    assert(!f.w.hud.BHasClass(gateClass)); assert(f.w.hud.BHasClass('gScoreboardOpen')); assert.equal(f.w.hud.visible, true);
});
check('a new loader clears the old gate and stale callbacks cannot re-hide it', () => {
    const f = fixture(); f.boot(); assert(!f.w.hud.BHasClass(gateClass)); f.advance(35); assert(!f.w.hud.BHasClass(gateClass));
});
check('ApplyStyles failure still restores the private class', () => {
    const f = fixture({ styleThrows: true }); f.advance(35); assert(!f.w.hud.BHasClass(gateClass));
    assert(Number(f.b.__dltLabel.GetAttributeString('dlt_native_reflow_flags', '0')) & 16);
});
check('missing ApplyStyles keeps the bounded CSS transition and restoration', () => {
    const f = fixture({ hasStyles: false }); f.advance(35); assert(!f.w.hud.BHasClass(gateClass));
    assert.equal(Number(f.b.__dltLabel.GetAttributeString('dlt_native_reflow_flags', '0')), 7);
});
check('short scheduling failure restores immediately', () => { const f = fixture({ scheduleThrows: true }); assert(!f.w.hud.BHasClass(gateClass)); assert.equal(f.gates().length, 1); });
check('a lost short callback is recovered by the existing 200ms loop', () => { const f = fixture({ dropShort: true }); assert(f.w.hud.BHasClass(gateClass)); f.advance(); assert(!f.w.hud.BHasClass(gateClass)); });
check('destroying the native root during its gate does not touch another root', () => {
    const f = fixture(), old = f.w.hud; f.w.destroy(old); f.advance(35); assert(!f.w.root.BHasClass(gateClass));
});
check('disabling translation during a gate cannot resurrect the text', () => {
    const f = fixture(); f.w.config.enabled = false; f.w.refresh(); f.advance(35);
    assert(!f.w.hud.BHasClass(gateClass)); assert.equal(f.b.__dltLabel.text, ''); assert.equal(f.b.__dltLabel.style.opacity, '0'); assert(!f.b.__dltLabel.BHasClass('DltNativeReady'));
});
check('no original message or parent geometry is modified', () => {
    const f = fixture(), source = f.b.FindChildTraverse('MessageText');
    assert.equal(source.text, 'root layout check'); assert.equal(f.b.FindChildTraverse('MessageContents').style.height, undefined);
    assert.equal(source.GetParent().BHasClass('MessageWrapper'), true);
});
check('nudge outcome reports measured recovery without a visibility gate', () => {
    const f=fixture({stopAtNudge:true}), label=f.b.__dltLabel;
    assert.equal(label.GetAttributeString('dlt_native_nudge_state',''), '1');
    label.actuallayoutwidth=120;label.actuallayoutheight=40;
    f.advance(60);f.advance(200);for(let i=0;i<8;i++)f.advance();
    assert.equal(label.GetAttributeString('dlt_native_nudge_state',''),'3');assert.equal(f.gates().length,0);
    const events=f.w.packets.flatMap(p=>p.logs||[]);assert(events.some(e=>e.code==='hud-nudge-start'));assert(events.some(e=>e.code==='hud-nudge-recovered'&&e.layout.labelWidth===120&&e.layout.labelHeight===40));
});
check('failed nudge logs dimensions before fallback instead of claiming it recovered',()=>{
    const f=fixture();for(let i=0;i<8;i++)f.advance();
    assert.equal(f.b.__dltLabel.GetAttributeString('dlt_native_nudge_state',''),'4');
    const events=f.w.packets.flatMap(p=>p.logs||[]);assert(events.some(e=>e.code==='hud-nudge-failed'));assert(!events.some(e=>e.code==='hud-nudge-recovered'));
});
check('root size nudge preserves existing code widths after restore',()=>{
    const f=fixture({zero:false});f.w.hud.style.width='90%';f.w.hud.style.height='600px';
    const label=f.b.__dltLabel;label.actuallayoutwidth=label.actuallayoutheight=0;
    for(let i=0;i<10;i++)f.advance();
    assert.equal(f.w.hud.style.width,'90%');assert.equal(f.w.hud.style.height,'600px');
});
check('missing resize API preserves one bounded fallback with explicit unavailable evidence',()=>{
    const f=fixture({missingClear:true});for(let i=0;i<8;i++)f.advance();
    assert.equal(f.gates().length,1);assert.equal(f.b.__dltLabel.GetAttributeString('dlt_native_nudge_state',''),'5');
    const events=f.w.packets.flatMap(p=>p.logs||[]);assert(events.some(e=>e.code==='hud-nudge-failed'&&e.count===5));assert(!events.some(e=>e.code==='hud-nudge-start'));
});
const report = { passed, failed: 0, realGame: false, rootCycleModel: true, rowCycleFirst: true, gpuVisibilityUnverified: true };
fs.writeFileSync(path.join(__dirname, '../artifacts/native-root-reflow-tests.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report));
