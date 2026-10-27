// Simulated native chat controller. The submit target is taken from an independent
// internal channel state, never from ChatTargetLabel.text, so a stale or lying label
// cannot make this test pass. Empty submit / blur reset the internal target to ALL,
// which is the reported real-world failure that blocked team sends.
const vm = require('node:vm'), fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');

const CHAT_SCRIPT = fs.readFileSync(path.join(__dirname, '../mod/panorama/scripts/dlt_chat.js'), 'utf8');
const TARGET_NAME = { team: 'ChatTarget_GameAllies', all: 'ChatTarget_GameAll', party: 'ChatTarget_Party' };
const TARGET_LABEL = { team: 'To (ALLIES):', all: 'To (ALL):', party: 'To (PARTY):' };
const results = [];
function check(name, fn) { try { fn(); results.push({ name, ok: true }); } catch (e) { results.push({ name, ok: false, error: String((e && e.message) || e) }); } }

class Panel {
    constructor(type, id, parent, cls = '') {
        this.type = type; this.id = id; this.parent = parent; this.classes = new Set(cls.split(' '));
        this.children = []; this.text = ''; this.style = {}; this.alive = true; this.focused = false;
        if (parent) parent.children.push(this);
    }
    IsValid() { return this.alive; }
    GetParent() { return this.parent; }
    BHasClass(c) { return this.classes.has(c); }
    AddClass(c) { this.classes.add(c); }
    BHasKeyFocus() { return this.focused === true; }
    GetChildCount() { return this.children.length; }
    GetChild(i) { return this.children[i]; }
    FindChildTraverse(id) { if (this.id === id) return this; for (const c of this.children) { const f = c.FindChildTraverse(id); if (f) return f; } return null; }
    FindChildrenWithClassTraverse(c) { return this.children.flatMap(x => [...(x.classes.has(c) ? [x] : []), ...x.FindChildrenWithClassTraverse(c)]); }
    SetURL(url) { return urlHandler(this, url); }
    SetPanelEvent(name, fn) { (this.events ||= {})[name] = fn; }
    SetMaxChars(limit) { this.maxChars = limit; }
    GetPositionWithinWindow() { if (!this.position) throw Error('geometry unavailable'); return this.position; }
    DeleteAsync() { this.alive = false; }
}
let urlHandler = () => {};

function world(opts = {}) {
    const w = { now: 100000, tick: null, trigger: 0, matchId: '1', serverId: 'test-server-1', calls: [], msgs: [], packets: [], queueMirror: [], submitted: [], waiting: [], synthetic: [], jobs: {} };
    w.chat = { target: TARGET_NAME.all, readonly: !!opts.readonly, available: !opts.nativeClasses && opts.available !== false, writes: [] };
    w.closeRefusals = opts.stubbornClose || 0;   // the game ignoring the native close this many times
    w.closeIgnored = 0;
    w.ignoreClose = false;
    w.config = Object.assign({
        enabled: true, translateOwn: false, fontSize: 17, scanIntervalMs: 200, transport: 'fragment',
        outgoingEnabled: true, outgoingTimeoutSeconds: opts.timeout || 30, incomingScope: 'all_players',
        outgoingDisplayMode: opts.displayMode || 'translation'
    }, opts.config || {});
    w.deferResult = opts.deferResult !== false;
    w.spec = opts.spec || {};
    w.setTarget = (name) => { w.chat.target = TARGET_NAME[name]; target.text = TARGET_LABEL[name]; if(opts.nativeClasses){for(const value of Object.values(TARGET_NAME))context.classes.delete(value);context.classes.add(TARGET_NAME[name]);} };
    w.resetTarget = () => { if(!opts.nativeClasses){w.chat.target = TARGET_NAME.all; target.text = TARGET_LABEL.all;} };
    w.pushResults = (items) => { w.synthetic.push(...items); };

    const root = new Panel('root', 'root');
    const context = new Panel('CitadelChat', 'Chat', root);
    new Panel('Panel', 'ChatMessages', context);
    const bridge = new Panel('HTML', 'DltBridge', context);
    const input = new Panel('TextEntry', 'ChatInput', context);
    let nativeInput = '';
    Object.defineProperty(input, 'text', {
        get() { return nativeInput; },
        set(value) { nativeInput = Array.from(String(value)).slice(0, opts.inputLimit || 80).join(''); }
    });
    const target = new Panel('Label', 'ChatTargetLabel', context);
    const sub = new Panel('Panel', 'SubOptions', root);
    new Panel('Panel', 'DltMenuMount', sub);
    target.text = TARGET_LABEL.all;
    Object.defineProperty(context, 'chat_target', {
        configurable: true,
        get() { return w.chat.available ? w.chat.target : undefined; },
        set(value) {
            if (!w.chat.available) return;
            w.chat.writes.push({ value: String(value), expanded: context.classes.has('ChatExpanded') });
            if (w.chat.readonly) return;
            w.chat.target = String(value);
            const name = Object.keys(TARGET_NAME).find(k => TARGET_NAME[k] === String(value)) || 'all';
            target.text = TARGET_LABEL[name];
        }
    });
    w.root = root; w.context = context; w.input = input; w.target = target; w.bridge = bridge;
    if (opts.draftOverlay) {
        root.position = { x: 0, y: 0 }; root.actualuiscale_x = root.actualuiscale_y = opts.uiScale || 1;
        input.position = { x: 40, y: 900 }; input.actuallayoutwidth = 600;
        Panel.prototype.SetFocus = function () {
            if (opts.rejectDraftFocus && this.id === 'DltOutgoingDraftInput') return false;
            const nativeHadFocus=input.focused;
            const visit = panel => { panel.focused = false; panel.children.forEach(visit); }; visit(root);
            this.focused = true;
            if(nativeHadFocus && this!==input) sandbox.$.DispatchEvent('CitadelChatInputBlur',input);
            return true;
        };
    }
    w.draft = () => root.FindChildTraverse('DltOutgoingDraftInput');
    w.setSettingsOpen = open => { root.__dltActiveSettingsWindow = open ? {IsValid:()=>true,__dltClosing:false} : null; };
    w.draftHost = () => root.FindChildTraverse('DltOutgoingDraftHost');
    w.draftHint = () => root.FindChildTraverse('DltOutgoingDraftHint')?.text;
    w.pressDraft = event => w.draft()?.events?.[event]?.();
    const hud = new Panel('Panel', 'Team1Chat', root);
    const hudRows = new Panel('Panel', 'Messages', hud);
    w.addEcho = (kind, channel = 'team', own = true, value = 'Wait for my ult') => {
        const parent = kind === 'hud' ? hudRows : context.FindChildTraverse('ChatMessages');
        const row = new Panel('Panel', '', parent, 'ChatMessage' + (own ? ' IsSelf' : ''));
        const body = new Panel('Panel', '', row, 'MessageBody');
        const source = new Panel('Panel', 'MessageSource', body);
        new Panel('Label', '', source, 'ChannelName').text = TARGET_LABEL[channel];
        const contents = new Panel('Panel', 'MessageContents', body);
        if (kind === 'hud') new Panel('Panel', '', contents, 'ChatBubble');
        new Panel('Label', kind === 'hud' ? 'MessageText' : '', contents).text = value;
        return row;
    };

    w.onSetURL = (panel, url) => {
        if (!url.includes('#') && !url.includes('?')) { panel.title = 'DLT:ready'; return; }
        const data = JSON.parse(decodeURIComponent(url.split('#')[1] || url.split('?d=')[1]));
        w.packets.push(data);
        const batch = data.outgoing || [];
        if (batch.length) w.queueMirror = batch.slice();
        if (data.outgoingWaiting && data.outgoingWaiting.length) w.waiting.push(...data.outgoingWaiting);
        for (const item of batch) if (!w.jobs[item.id]) w.jobs[item.id] = { item, state: item.holdForInput ? 'waiting_input' : 'queued', holding: !!item.holdForInput };
        for (const status of data.outgoingStatus || []) if (status.state === 'capture_ready' && w.jobs[status.id]) { w.jobs[status.id].holding = false; w.jobs[status.id].state = 'queued'; }
        for (const ack of data.outgoingSent || []) if (w.jobs[ack]) w.jobs[ack].state = 'submitted';
        for (const refusal of data.outgoingRefused || []) if (w.jobs[refusal.id] && w.jobs[refusal.id].state !== 'submitted') w.jobs[refusal.id].state = 'failed';
        for (const held of data.outgoingWaiting || []) if (w.jobs[held] && w.jobs[held].state !== 'submitted') w.jobs[held].state = 'waiting_channel';
        const outgoing = [];
        // A real backend keeps returning a ready job until the client acknowledges the send.
        if (!w.deferResult) for (const job of Object.values(w.jobs)) if (job.state !== 'submitted') outgoing.push(w.resultFor(job.item));
        outgoing.push(...w.synthetic.splice(0, w.synthetic.length));
        if (w.dropReleaseOnce && (data.outgoingStatus||[]).some(s=>s.state==='capture_ready')) { w.dropReleaseOnce=false; return; }
        panel.title = 'DLT:' + JSON.stringify({
            seq: data.seq, serverId: w.serverId, results: [], hotkey: w.trigger > data.hotkeySeen ? w.trigger : 0,logAck:data.logs?.at(-1)?.seq||0,
            outgoing, config: w.config, configuredLlm: false
            ,capabilities: [...(opts.noPartyCapability ? ['outgoing-display-mode'] : opts.noLanguageCapability ? ['outgoing-display-mode','outgoing-party-channel'] : ['outgoing-display-mode','outgoing-party-channel','multilingual-targets']),...(opts.diagnostics?['diagnostic-logs']:[]),...(opts.statusCapability?['outgoing-status-v1']:[])]
        });
    };
    urlHandler = w.onSetURL;
    w.resultFor = (item) => {
        if (w.jobs[item.id]?.holding) return {id:item.id,channel:item.channel,displayMode:item.displayMode,state:'waiting_input',sendText:'',english:''};
        const spec = w.spec[item.id] || {};
        const mode = item.displayMode === 'bilingual' ? 'bilingual' : 'translation';
        const english = spec.english !== undefined ? spec.english : 'Wait for my ult';
        const sendText = spec.sendText !== undefined ? spec.sendText : (mode === 'bilingual' ? '等我大招 | Wait for my ult' : english);
        return { id: item.id, channel: item.channel, displayMode: mode, english,
            sendText: spec.omitSendText ? undefined : sendText, state: spec.state || (['waiting_channel','failed'].includes(w.jobs[item.id]?.state) ? w.jobs[item.id].state : 'ready'), error: spec.error };
    };

    const sandbox = {
        Game: { GetMatchID: () => w.matchId },
        Object: opts.observeKeys ? {create:Object.create,keys(value){const keys=Object.keys(value);opts.observeKeys(value,keys);return keys;}} : Object,
        Date: { now: () => w.now }, Math, JSON, String, encodeURIComponent,
        $: {
            GetContextPanel: () => context,
            Schedule: (s, fn) => { w.nextDelay=s; w.tick = fn; },
            Msg: (m) => { w.msgs.push(String(m)); },
            DispatchEvent: (name, ...args) => {
                w.calls.push({ name, args, at: w.now });
                // An ignored close means neither the empty submit nor its blur collapsed the box.
                const closeIgnored = () => { if (w.closeRefusals > 0) { w.closeRefusals--; w.closeIgnored++; w.ignoreClose = true; return true; } return false; };
                if (name === 'CitadelChatInputSubmitted') {
                    if (args[0] !== input) return false;
                    const value = input.text;
                    if (value && opts.ignoreSubmitOnce && !w.ignoredSubmit) { w.ignoredSubmit = true; return true; }
                    if(value && opts.rejectSubmit)return false;
                    if (!value || !opts.keepSubmittedText) input.text = '';
                    if (!value && closeIgnored()) return true;           // ignored empty close, not a payload receipt
                    if (!value || !opts.keepNativeExpanded) context.classes.delete('ChatExpanded'); input.focused = false;
                    if (!value) { w.resetTarget(); return true; }        // native close resets the target
                    w.submitted.push({ text: value, channel: w.chat.target, at: w.now });
                    return opts.undefinedDispatch ? undefined : true;
                }
                if (name === 'CitadelChatInputBlur') {
                    if (w.ignoreClose) { w.ignoreClose = false; return true; }
                    context.classes.delete('ChatExpanded'); input.focused = false; w.resetTarget(); return true;
                }
                if (name === 'CitadelConCommand') return false;
                return true;
            },
            CreatePanel: (type, parent, id) => new Panel(type, id, parent)
        }
    };
    vm.runInNewContext(CHAT_SCRIPT, sandbox);
    w.step = (n = 1) => { for (let i = 0; i < n; i++) { w.now += 100; if (w.tick) w.tick(); } };
    w.openChat = (channel) => { if (channel) w.setTarget(channel); context.classes.add('ChatExpanded'); input.focused = true; if(!opts.noFocusEvent&&sandbox.DLTChatInputFocused)sandbox.DLTChatInputFocused(); return input; };
    w.closeChat = () => { context.classes.delete('ChatExpanded'); input.focused = false; };
    w.blurNative = () => sandbox.$.DispatchEvent('CitadelChatInputBlur',input);
    w.shortcut = text => { input.text = text; w.trigger++; };
    w.enterNative = () => sandbox.$.DispatchEvent('CitadelChatInputSubmitted',input);
    w.capture = (text) => { w.shortcut(text); w.step(9); };
    return w;
}
const channelOfTarget = (name) => Object.keys(TARGET_NAME).find(k => TARGET_NAME[k] === name) || name;

// Current native controller exports its cached target using an engine-maintained CSS
// class group, not a JavaScript chat_target property. Closing input keeps that target.
for(const channel of ['team','all','party']){
    const w=world({nativeClasses:true,displayMode:'bilingual'});w.step(60);w.openChat(channel);w.capture('等我大招');
    w.target.text=TARGET_LABEL[channel==='team'?'all':'team'];
    w.deferResult=false;w.step(20);
    check('engine target class sends '+channel+' after close with no JS target property',()=>{
        assert.equal(w.submitted.length,1);assert.equal(channelOfTarget(w.submitted[0].channel),channel);
        assert.equal(w.submitted[0].text,'等我大招 | Wait for my ult');assert.equal(w.chat.writes.length,0);
    });
}
for(const channel of ['team','all','party']){
    const w=world({nativeClasses:true,diagnostics:true});w.step(60);w.openChat(channel);w.capture('关闭后样式消失');
    w.context.classes.delete(TARGET_NAME[channel]);w.deferResult=false;w.step(25);
    check('captured engine evidence survives presentation-class loss for '+channel,()=>{
        assert.equal(w.submitted.length,1);assert.equal(channelOfTarget(w.submitted[0].channel),channel);assert.equal(w.chat.writes.length,0);
        const events=w.packets.flatMap(p=>p.logs||[]);assert(events.some(e=>e.code==='channel-cached'));
    });
}
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('old draft');
    w.openChat('all');w.closeChat();w.context.classes.delete(TARGET_NAME.all);w.deferResult=false;w.step(25);
    check('even a rapid reopen-and-close invalidates the captured native proof',()=>{assert.equal(w.submitted.length,0);assert(w.waiting.includes('out-1'));});
}
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('private');
    w.context.classes.add(TARGET_NAME.all);w.deferResult=false;w.step(20);
    check('contradictory engine classes invalidate proof instead of authorizing a cached send',()=>{assert.equal(w.submitted.length,0);assert(w.waiting.length);});
    w.context.classes.delete(TARGET_NAME.all);w.context.classes.delete(TARGET_NAME.team);w.step(20);
    check('removing contradictory classes cannot resurrect an invalidated channel proof',()=>assert.equal(w.submitted.length,0));
}
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('saved');
    w.context.classes.delete(TARGET_NAME.team);w.input.alive=false;w.deferResult=false;w.step(20);
    check('an invalidated input control cannot reuse captured channel evidence',()=>assert.equal(w.submitted.length,0));
}
{
    const w=world({nativeClasses:true,noFocusEvent:true});w.step(60);w.openChat('team');w.capture('missing hook');w.context.classes.delete(TARGET_NAME.team);w.deferResult=false;w.step(25);
    check('a missing native focus hook cannot authorize cached-channel fallback',()=>{assert.equal(w.submitted.length,0);assert(w.waiting.length);});
}
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('等我大招');
    w.openChat('all');w.input.text='new draft';w.deferResult=false;w.step(20);
    check('a newly opened different channel is not modified or sent into',()=>{assert.equal(w.submitted.length,0);assert.equal(w.input.text,'new draft');});
    w.input.text='';w.closeChat();w.step(20);
    check('different native target stays unsent instead of relabeling CSS',()=>{assert.equal(w.submitted.length,0);assert(w.context.classes.has(TARGET_NAME.all));});
    w.openChat('team');w.closeChat();w.step(20);
    check('returning to the original native target resumes the waiting task once',()=>{assert.equal(w.submitted.length,1);assert.equal(channelOfTarget(w.submitted[0].channel),'team');});
}

// ---------------------------------------------------------------- team + translation
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('party');w.target.text='对（队伍）：';w.capture('hello');w.deferResult=false;w.step(20);
    check('Chinese party input closes and sends translation only to PARTY',()=>{assert(!w.context.classes.has('ChatExpanded'));assert.equal(w.submitted.length,1);assert.equal(w.submitted[0].channel,TARGET_NAME.party);});
}
{
    const w=world({nativeClasses:true,noPartyCapability:true});w.step(60);w.openChat('party');w.capture('keep draft');
    check('old backend without PARTY capability leaves the draft untouched',()=>{assert.equal(w.input.text,'keep draft');assert.equal(w.queueMirror.length,0);assert(w.context.classes.has('ChatExpanded'));});
}
{
    const w=world({nativeClasses:true,rejectSubmit:true});w.step(60);w.openChat('team');w.capture('test');w.deferResult=false;w.step(20);
    check('a rejected native submit is reported as failed without retrying',()=>{
        assert.equal(w.submitted.length,0);assert(w.packets.some(p=>(p.outgoingRefused||[]).some(r=>r.reason==='submit-rejected')));
        assert.equal(w.jobs[w.queueMirror[0].id].state,'failed');
    });
}
{
    const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('test');
    w.context.classes.add(TARGET_NAME.all);w.deferResult=false;w.step(20);
    check('contradictory engine target classes never authorize a send',()=>assert.equal(w.submitted.length,0));
}
{
    const w = world();
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    check('team capture closes the input and queues one team task', () => {
        assert.equal(w.input.text, '');
        assert(!w.context.classes.has('ChatExpanded'));
        assert.equal(w.queueMirror.length, 1);
        assert.equal(w.queueMirror[0].channel, 'team');
        assert.equal(w.queueMirror[0].displayMode, 'translation');
    });
    check('team translation is submitted once and reaches the team channel', () => {
        w.deferResult = false; w.step(20);
        assert.equal(w.submitted.length, 1, 'expected exactly one native submit, got ' + JSON.stringify(w.submitted));
        assert.equal(channelOfTarget(w.submitted[0].channel), 'team', 'native submit target must be team');
        assert.equal(w.submitted[0].text, 'Wait for my ult');
    });
    check('temporarily changed channel is restored after the submit', () => {
        assert.equal(channelOfTarget(w.chat.target), 'all');
        assert(w.chat.writes.length >= 1, 'the native target must have been set through the readable property');
        assert(w.chat.writes.every(x => x.expanded === false), 'the channel is only redirected while the box is closed: ' + JSON.stringify(w.chat.writes));
    });
    check('a duplicate ready result never triggers a second submit', () => {
        w.pushResults([w.resultFor({ id: w.queueMirror[0].id, channel: 'team', displayMode: 'translation' })]);
        w.step(20);
        assert.equal(w.submitted.length, 1);
    });
}

// ---------------------------------------------------------------- team + bilingual
{
    const w = world({ displayMode: 'bilingual', spec: { 'out-1': { sendText: '等我大招 | Wait for my ult' } } });
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    check('bilingual capture carries the captured display mode', () => {
        assert.equal(w.queueMirror[0].displayMode, 'bilingual');
    });
    check('bilingual sends exactly one combined message to the team channel', () => {
        w.deferResult = false; w.step(20);
        assert.equal(w.submitted.length, 1);
        assert.equal(channelOfTarget(w.submitted[0].channel), 'team');
        assert.equal(w.submitted[0].text, '等我大招 | Wait for my ult');
    });
}

// ---------------------------------------------------------------- all channel regression
for (const mode of ['translation', 'bilingual']) {
    const w = world({ displayMode: mode, spec: { 'out-1': { sendText: mode === 'bilingual' ? 'push mid | Push mid' : 'Push mid' } } });
    w.step(60);
    w.openChat('all');
    w.capture('push mid');
    w.deferResult = false; w.step(20);
    check('all + ' + mode + ' still reaches the all channel', () => {
        assert.equal(w.submitted.length, 1);
        assert.equal(channelOfTarget(w.submitted[0].channel), 'all');
        assert.equal(w.submitted[0].text, mode === 'bilingual' ? 'push mid | Push mid' : 'Push mid');
    });
}

// ---------------------------------------------------------------- no writable native target
{
    const w = world({ readonly: true });
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.deferResult = false; w.step(30);
    check('team task without a reliable channel interface is never sent to all', () => {
        assert.equal(w.submitted.length, 0, 'must not submit when the channel cannot be confirmed');
    });
    check('unconfirmed team task is reported as waiting for the channel', () => {
        assert(w.waiting.includes('out-1'), 'client must report outgoingWaiting for out-1');
    });
    check('unconfirmed team task keeps the original for the settings page', () => {
        const accepted = w.packets.flatMap(p => p.outgoing || []);
        assert.equal(accepted.length, 1);
        assert.equal(accepted[0].text, '等我大招');
    });
    check('task is sent once the channel is confirmed again', () => {
        w.openChat('team'); w.closeChat(); w.step(4);
        assert.equal(w.submitted.length, 1, 'a later confirmed channel may still submit the preserved task');
        assert.equal(channelOfTarget(w.submitted[0].channel), 'team');
    });
}

// ---------------------------------------------------------------- label conflicts with internal mode
{
    const w = world({ readonly: true });
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.target.text = TARGET_LABEL.team;                 // label lies: internal target is still ALL
    w.deferResult = false; w.step(30);
    check('a lying label cannot make the client send to the wrong channel', () => {
        assert.equal(w.submitted.length, 0);
    });
}

// ---------------------------------------------------------------- property unavailable
{
    const w = world({ available: false });
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.deferResult = false; w.step(30);
    check('team task stays unsent when no native channel property exists', () => {
        assert.equal(w.submitted.length, 0);
        assert(w.waiting.includes('out-1'));
    });
}

// ---------------------------------------------------------------- drafts and channel switches
{
    const w = world();
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.deferResult = false;
    check('a new draft blocks submission and is preserved', () => {
        w.input.text = 'a new unsent draft';
        w.openChat();                       // player reopened the box and is typing
        w.step(15);
        assert.equal(w.submitted.length, 0);
        assert.equal(w.input.text, 'a new unsent draft');
        w.closeChat(); w.step(15);
        assert.equal(w.submitted.length, 1, 'once idle the queued task submits');
        assert.equal(w.input.text, 'a new unsent draft', 'the new draft must be restored');
        assert.equal(channelOfTarget(w.submitted[0].channel), 'team');
    });
}

// ---------------------------------------------------------------- two identical proactive sends
{
    const w = world();
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.deferResult = false; w.step(20);
    w.openChat('team');
    w.capture('等我大招');
    w.step(20);
    check('two proactive sends of the same text stay two independent tasks', () => {
        assert.equal(w.submitted.length, 2, 'dedupe must be per task id, not per text');
        assert.deepEqual(w.submitted.map(s => channelOfTarget(s.channel)), ['team', 'team']);
    });
}

// ---------------------------------------------------------------- capture-time display mode
{
    const w = world();
    w.step(60);
    w.openChat('team');
    w.capture('first');
    check('mode is captured at keypress time', () => {
        assert.equal(w.queueMirror[0].displayMode, 'translation');
    });
    w.config.outgoingDisplayMode = 'bilingual';
    w.openChat('team');
    w.capture('second');
    check('a later task uses the new mode and keeps the older task snapshot', () => {
        assert.equal(w.queueMirror[0].displayMode, 'bilingual');
        assert.equal(w.packets.flatMap(p => p.outgoing || []).filter(i => i.text === 'first')[0].displayMode, 'translation');
    });
}

// ---------------------------------------------------------------- length and content guards
{
    const value = '😀'.repeat(80), w = world({ spec: { 'out-1': { sendText: value } } });
    w.step(60); w.openChat('all'); w.capture('unicode'); w.deferResult = false; w.step(20);
    check('80 supplementary Unicode characters fit the native entry without UTF-16 miscount', () => {
        assert.equal(w.submitted.length, 1); assert.equal(w.submitted[0].text, value);
    });
}
{
    const w = world({ inputLimit: 60, spec: { 'out-1': { sendText: 'x'.repeat(80) } } });
    w.step(60); w.openChat('party'); w.capture('future smaller limit'); w.deferResult = false; w.step(20);
    check('native readback rejects an unexpectedly truncated value before dispatch', () => {
        assert.equal(w.submitted.length, 0);
        assert(w.packets.flatMap(p => p.outgoingRefused || []).some(r => r.id === 'out-1' && r.reason === 'input-truncated'));
        assert.equal(w.input.text, ''); assert.equal(channelOfTarget(w.chat.target), 'all');
    });
    w.step(30);
    check('readback failure never retries or sends the shortened prefix', () => assert.equal(w.submitted.length, 0));
}
{
    const w = world({ spec: { 'out-1': { sendText: 'broken\ud800' } } });
    w.step(60); w.openChat('all'); w.capture('invalid unicode'); w.deferResult = false; w.step(20);
    check('malformed Unicode is refused without passing a surrogate to the game', () => {
        assert.equal(w.submitted.length, 0);
        assert(w.packets.flatMap(p => p.outgoingRefused || []).some(r => r.reason === 'invalid-send-text'));
    });
}
{
    const w = world({ spec: { 'out-1': { sendText: 'x'.repeat(80) } } });
    w.step(60);
    w.openChat('all');
    w.capture('long but legal');
    w.deferResult = false; w.step(20);
    check('an 80 character final message is submitted intact through the native cap', () => { assert.equal(w.submitted.length, 1); assert.equal(w.submitted[0].text, 'x'.repeat(80)); });
}
{
    const w = world({ spec: { 'out-1': { sendText: 'x'.repeat(81) } } });
    w.step(60);
    w.openChat('all');
    w.capture('too long');
    w.deferResult = false; w.step(20);
    check('an over long final message is never truncated or sent', () => {
        assert.equal(w.submitted.length, 0);
    });
    check('an over long final message is reported as refused', () => {
        const refused = w.packets.flatMap(p => p.outgoingRefused || []);
        assert(refused.some(r => r.id === 'out-1' && r.reason === 'invalid-send-text'), JSON.stringify(refused));
    });
}
{
    const w = world({ displayMode: 'bilingual', spec: { 'out-1': { omitSendText: true } } });
    w.step(60);
    w.openChat('all');
    w.capture('中文 😀 混排');
    w.deferResult = false; w.step(20);
    check('a bilingual result without SendText is not silently sent as English only', () => {
        assert.equal(w.submitted.length, 0, 'an outdated backend must not fall back to translation-only');
    });
    check('the refusal is reported with a reason instead of leaving the task looking ready', () => {
        const refused = w.packets.flatMap(p => p.outgoingRefused || []);
        assert(refused.some(r => r.id === 'out-1' && r.reason === 'backend-outdated'), JSON.stringify(refused));
    });
}
{
    const w = world({ spec: { 'out-1': { sendText: 'safe" ; \\ text' } } });
    w.step(60);
    w.openChat('all');
    w.capture('quote test');
    w.deferResult = false; w.step(20);
    check('residual command delimiters are refused a second time on the game side', () => {
        assert.equal(w.submitted.length, 0);
    });
}

// ---------------------------------------------------------------- chord timing and close reliability
{
    const w = world();
    w.step(60);
    w.input.text = '等我大招';           // the player typed in the box
    w.trigger++;                         // the native hook fired
    w.step(6);                           // the bridge has not reported the open box yet
    check('a chord arriving before the chat is reported open is held, not dropped', () => {
        assert.equal(w.queueMirror.length, 0, 'nothing may be queued before the box is known to be open');
        assert.equal(w.input.text, '等我大招', 'the draft is untouched');
    });
    w.openChat('team');
    w.step(4);
    check('the held chord captures as soon as the chat box is known to be open', () => {
        assert.equal(w.queueMirror.length, 1);
        assert.equal(w.queueMirror[0].channel, 'team');
        assert.equal(w.input.text, '');
        assert(!w.context.classes.has('ChatExpanded'));
    });
    check('the held chord is captured exactly once', () => {
        w.deferResult = false; w.step(20);
        assert.equal(w.submitted.length, 1);
    });
}
{
    const w = world();
    w.step(60);
    w.openChat('team');
    w.trigger++;                         // an empty box must not queue anything
    w.step(10);
    check('an empty chat box produces no task and no submit', () => {
        assert.equal(w.packets.flatMap(p => p.outgoing || []).length, 0);
        assert.equal(w.submitted.length, 0);
    });
}
{
    const w = world({ stubbornClose: 1 });    // the game ignores the first native close
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    w.step(2);                             // close retry is a separate settled callback
    check('the box is closed even when the native empty submit is ignored once', () => {
        assert(!w.context.classes.has('ChatExpanded'), 'the close must be retried until the box collapses');
        assert.equal(w.closeIgnored, 1, 'exactly one ignored close is expected, got ' + w.closeIgnored);
        assert.equal(w.queueMirror.length, 1);
    });
    check('the captured channel is restored only after the box actually collapsed', () => {
        w.deferResult = false; w.step(20);
        assert.equal(w.submitted.length, 1);
        assert.equal(channelOfTarget(w.submitted[0].channel), 'team');
        assert(w.chat.writes.length >= 1 && w.chat.writes.every(x => x.expanded === false), JSON.stringify(w.chat.writes));
    });
}
{
    const w = world({ stubbornClose: 99 });   // the game never collapses the box
    w.step(60);
    w.openChat('team');
    w.capture('等我大招');
    check('a box that stays open gets only bounded close retries', () => {
        assert(w.closeIgnored >= 1 && w.closeIgnored <= 3, 'bounded retries only, got ' + w.closeIgnored);
        assert.equal(w.queueMirror.length, 0, 'snapshot must wait for stable close confirmation');
    });
    w.input.text = 'a new draft';             // the player keeps the box and types again
    w.step(10);
    check('a new draft in a still-open box is never submitted or cleared', () => {
        assert.equal(w.input.text, 'a new draft');
        assert.equal(w.submitted.length, 0);
        assert(w.closeIgnored <= 3, 'no further close attempts, got ' + w.closeIgnored);
    });
}

// ---------------------------------------------------------------- review regressions
{
    const w = world({ available: false });
    w.step(60); w.openChat('team'); w.capture('private team message');
    w.target.text = TARGET_LABEL.team; // label remained stale after internal target reset to all
    w.deferResult = false; w.step(30);
    check('an unavailable native target cannot be confirmed by a stale label', () => {
        assert.equal(w.chat.target, TARGET_NAME.all);
        assert.equal(w.submitted.length, 0);
        assert(w.waiting.includes('out-1'));
    });
}
{
    const w = world({ readonly: true });
    w.step(60); w.openChat('team'); w.capture('held team message');w.deferResult=false;w.step(30);
    check('the mock backend actually returns waiting_channel', () => assert.equal(w.jobs['out-1'].state,'waiting_channel'));
    w.chat.readonly=false;w.setTarget('team');w.step(30);
    check('a real waiting_channel result is rechecked and sent exactly once', () => {
        assert.equal(w.submitted.length,1);assert.equal(w.submitted[0].channel,TARGET_NAME.team);
        assert.equal(w.jobs['out-1'].state,'submitted');
    });
}
for (const scenario of ['hud','foreign','old']) {
    const w=world();
    if(scenario==='old')w.addEcho('regular','all',true);
    w.step(60);w.openChat('team');w.capture('first');w.deferResult=false;w.step(20);
    if(scenario==='hud')w.addEcho('hud','team',true);
    if(scenario==='foreign')w.addEcho('regular','all',false);
    w.step(4);w.openChat('team');w.capture('second');w.step(25);
    check(scenario+' echo cannot falsely disable later sends',()=>{
        assert.equal(w.submitted.length,2);assert(!w.msgs.some(x=>x.includes('further sends are held')));
    });
}
{
    const w=world();w.step(60);w.openChat('team');w.capture('first');w.deferResult=false;w.step(20);
    w.addEcho('regular','all',true);w.step(4);w.openChat('team');w.capture('second');w.step(20);
    check('a new self echo in a definite wrong channel still stops subsequent sends',()=>{
        assert.equal(w.submitted.length,1);assert(w.msgs.some(x=>x.includes('team message appeared in all')));
    });
}

// ---------------------------------------------------------------- second review: cancellation boundaries
{
    const w=world({config:{outgoingQueueLimit:12}});w.step(60);
    for(let i=0;i<12;i++){w.openChat('team');w.capture('burst '+i);}
    check('configured capacity accepts twelve pending translations',()=>assert.equal(Object.keys(w.jobs).length,12));
    w.openChat('team');w.capture('preserve thirteenth draft');
    check('capacity rejection keeps the current draft and input open',()=>{assert.equal(w.input.text,'preserve thirteenth draft');assert(w.context.classes.has('ChatExpanded'));assert.equal(Object.keys(w.jobs).length,12);});
}
{
    const w=world();w.step(60);w.openChat('team');w.capture('disable while pending');
    w.config.outgoingEnabled=false;w.deferResult=false;w.step(25);
    check('turning off sending refuses already queued ready tasks',()=>{
        assert.equal(w.submitted.length,0);
        assert(w.packets.flatMap(p=>p.outgoingRefused||[]).some(x=>x.reason==='disabled'));
    });
    w.config.outgoingEnabled=true;w.step(20);
    check('reenabling does not replay the refused task',()=>assert.equal(w.submitted.length,0));
}
{
    const w=world();w.step(60);w.input.text='stale chord';w.trigger++;w.step(6);
    w.serverId='restarted-server';w.step(6);
    w.openChat('team');w.input.text='new draft after restart';w.step(8);
    check('backend restart clears held hotkeys without consuming a new draft',()=>{
        assert.equal(w.input.text,'new draft after restart');assert.equal(w.queueMirror.length,0);
    });
}
// ---------------------------------------------------------------- independent ESC menu
{
    const menuSource=fs.readFileSync(path.join(__dirname,'../mod/panorama/scripts/dlt_menu.js'),'utf8');
    const owner=new Panel('CitadelHudEscapeMenu','Escape');new Panel('Panel','EscapeBackground',owner);
    const menu=new Panel('Panel','Menu',owner), options=new Panel('Panel','SubOptions',menu);
    const context=new Panel('Panel','DltMenuMount',options);new Panel('Label','DltMenuStatus',context);
    let created=0,loaded='';
    const sandbox={$:{GetContextPanel:()=>context,Schedule:()=>{},CreatePanel:(type,parent,id)=>{
        created++;const p=new Panel(type,id,parent);p.BLoadLayout=url=>{loaded=url};return p;
    }}};
    vm.runInNewContext(menuSource,sandbox);
    check('ESC menu opens a single game window without using the browser bridge',()=>{
        sandbox.DLTMenuOpenSettings();sandbox.DLTMenuOpenSettings();
        assert.equal(created,1);assert(loaded.endsWith('/dlt_settings_window.xml'));
        assert.equal(owner.__dltSettingsWindow.parent,owner);
    });
}

// Optimization regressions: no real keyboard input or messages are sent.
check('ready bridge exchanges data without the old five-second hold',()=>{
 const w=world();w.step(4);assert(w.packets.length>0);assert(w.now<105000);
});
check('own bilingual echo is not sent back to cloud in either view',()=>{
 const w=world({displayMode:'bilingual',nativeClasses:true});w.step(60);w.openChat('team');w.capture('等我大招');w.deferResult=false;w.step(20);
 const line='等我大招 | Wait for my ult';w.addEcho('regular','team',true,line);w.addEcho('hud','team',true,line);w.step(8);
 assert(!w.packets.flatMap(p=>p.messages||[]).some(m=>m.text===line));
 w.addEcho('regular','team',false,line);w.step(8);assert(w.packets.flatMap(p=>p.messages||[]).some(m=>m.text===line),'another player is still translated');
});
check('own English-only echo reuses original without another cloud request',()=>{
 const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('等我大招');w.deferResult=false;w.step(20);
 w.addEcho('regular','team',true,'Wait for my ult');w.step(8);
 assert(!w.packets.flatMap(p=>p.messages||[]).some(m=>m.text==='Wait for my ult'));
});

check('send target language is captured with the hotkey and survives later settings changes',()=>{
 const w=world({nativeClasses:true,config:{outgoingTargetLanguage:'ru',outgoingCustomLanguage:'',outgoingCustomLanguageCode:''}});w.step(60);w.openChat('team');w.capture('等我大招');
 assert.equal(w.queueMirror[0].targetLanguage,'ru');w.config.outgoingTargetLanguage='ko';w.step(8);assert.equal(w.queueMirror[0].targetLanguage,'ru');
});
check('custom send name and service code reach the outgoing job',()=>{
 const w=world({nativeClasses:true,config:{outgoingTargetLanguage:'custom',outgoingCustomLanguage:'荷兰语',outgoingCustomLanguageCode:'nl'}});w.step(60);w.openChat('team');w.capture('等我大招');
 assert.equal(w.queueMirror[0].customLanguage,'荷兰语');assert.equal(w.queueMirror[0].customLanguageCode,'nl');
});
check('self echo does not reuse Chinese source when reception target is a different language',()=>{
 const w=world({nativeClasses:true,config:{incomingTargetLanguage:'ja'}});w.step(60);w.openChat('team');w.capture('等我大招');w.deferResult=false;w.step(20);
 w.addEcho('regular','team',true,'Wait for my ult');w.step(8);assert(w.packets.flatMap(p=>p.messages||[]).some(m=>m.text==='Wait for my ult'));
});
check('new language sending preserves the draft when an old backend lacks language support',()=>{
 const w=world({nativeClasses:true,noLanguageCapability:true,config:{outgoingTargetLanguage:'ru'}});w.step(60);w.openChat('team');w.capture('保留原文');assert.equal(w.queueMirror.length,0);assert.equal(w.input.text,'保留原文');assert(w.context.classes.has('ChatExpanded'));
});

check('multipart sends original then translation once with per-part acknowledgement',()=>{
 const w=world({nativeClasses:true,displayMode:'bilingual',config:{outgoingSeparateBilingual:true,outgoingSplitLong:true}});w.step(60);w.openChat('team');w.capture('原文');
 const item=w.queueMirror[0];assert.equal(item.separateBilingual,true);assert.equal(item.splitLong,true);
 const part=(index,text)=>({...w.resultFor(item),sendText:text,partIndex:index,partCount:2});
 w.pushResults([part(0,'原文')]);w.step(6);assert.deepEqual(w.submitted.map(x=>x.text),['原文']);
 w.pushResults([part(0,'原文')]);w.step(6);assert.equal(w.submitted.length,1);
 w.pushResults([part(1,'translation')]);w.step(8);assert.deepEqual(w.submitted.map(x=>x.text),['原文','translation']);
 assert(w.submitted.every(x=>channelOfTarget(x.channel)==='team'));
 const acks=w.packets.flatMap(x=>x.outgoingPartAcks||[]);assert(acks.some(x=>x.part===0)&&acks.some(x=>x.part===1));
 w.pushResults([part(1,'translation')]);w.step(8);assert.equal(w.submitted.length,2);
});
check('multipart defers while player edits a new message and preserves that draft',()=>{
 const w=world({nativeClasses:true,displayMode:'bilingual'});w.step(60);w.openChat('team');w.capture('原文');const item=w.queueMirror[0];
 w.pushResults([{...w.resultFor(item),sendText:'原文',partIndex:0,partCount:2}]);w.step(6);
 w.openChat('team');w.input.text='player is typing';
 w.pushResults([{...w.resultFor(item),sendText:'translation',partIndex:1,partCount:2}]);w.step(15);
 assert.equal(w.submitted.length,1);assert.equal(w.input.text,'player is typing');
 w.closeChat();w.pushResults([{...w.resultFor(item),sendText:'translation',partIndex:1,partCount:2}]);w.step(10);
 assert.equal(w.submitted.length,2);assert.equal(w.input.text,'player is typing');
});
check('preset delivered from backend needs no captured editor text and never duplicates',()=>{
 const w=world({nativeClasses:true});w.step(60);w.setTarget('team');
 const result={id:'preset-123',original:'group up',english:'group up',sendText:'group up',displayMode:'translation',channel:'team',state:'ready',isPreset:true,partIndex:0,partCount:1};
 w.pushResults([result]);w.step(10);assert.equal(w.submitted.length,1);assert.equal(w.submitted[0].text,'group up');
 w.pushResults([result]);w.step(15);assert.equal(w.submitted.length,1);assert.equal(w.queueMirror.length,0);
});
check('multipart does not accept a future part before the first part',()=>{
 const w=world({nativeClasses:true});w.step(60);w.openChat('team');w.capture('原文');const item=w.queueMirror[0];
 w.pushResults([{...w.resultFor(item),sendText:'future',partIndex:1,partCount:2}]);w.step(10);assert.equal(w.submitted.length,0);
});
const failed = results.filter(r => !r.ok);
const result = {
    passed: results.length - failed.length, total: results.length,
    scope: 'simulated native input submission with an independent internal channel; no real game send',
    failures: failed
};
fs.writeFileSync(path.join(__dirname, '../artifacts/outgoing-ui-tests.json'), JSON.stringify(result, null, 2));
console.log(JSON.stringify({ passed: result.passed, total: result.total }, null, 2));
for (const f of failed) console.error('FAIL: ' + f.name + ' :: ' + f.error);
if (failed.length) process.exit(1);
