const test = require('node:test');
const assert = require('node:assert/strict');
const world = require('./helpers/chat-world.cjs');

test('voice gate follows the local team and hides subtitles on lost connectivity', () => {
    const w = world();
    const topBar = new w.Panel('TopBar', w.root);
    w.root.children = w.root.children.filter(panel => panel !== w.chatContainer);
    w.chatContainer.parent = topBar;
    topBar.children.push(w.chatContainer);
    const teams = new w.Panel('TeamsContainer', topBar);
    const addTeam = (id, cls) => {
        const team = new w.Panel(id, teams, cls);
        team.paneltype = 'CitadelHudTopBarTeam';
        return new w.Panel('PlayersContainer', team);
    };
    const addPlayer = (team, cls) => {
        const player = new w.Panel('', team, cls);
        player.paneltype = 'CitadelHudTopBarPlayer';
        return player;
    };
    const one = addTeam('TeamFriendly', 'team1');
    const two = addTeam('TeamEnemy', 'team2');
    const enemy = addPlayer(one, 'VoiceActive');
    const local = addPlayer(two, 'LocalPlayer VoiceActive');
    const teammate = addPlayer(two, '');
    const packet = () => {
        w.step(18);
        return w.packets.at(-1);
    };
    assert.equal('teammateSpeaking' in packet(), false);
    w.voice = { enabled: true };
    assert.equal(packet().teammateSpeaking, false, 'enemy and self voices stay excluded');
    // Start between periodic heartbeats. Both edges of the short utterance
    // must go out on their next normal tick, including a release before 220 ms.
    const beforeHeartbeat = w.packets.length;
    for (let i = 0; i < 6 && w.packets.length === beforeHeartbeat; i++) w.step(1, 100);
    assert.ok(w.packets.length > beforeHeartbeat);
    teammate.AddClass('VoiceActive');
    w.step(1, 100);
    assert.equal(w.packets.at(-1).teammateSpeaking, true);
    teammate.cls.delete('VoiceActive');
    w.step(1, 100);
    assert.equal(w.packets.at(-1).teammateSpeaking, false);
    teammate.AddClass('PartyVoiceActive');
    assert.equal(packet().teammateSpeaking, true);
    teammate.cls.delete('PartyVoiceActive');
    assert.equal(packet().teammateSpeaking, false);
    enemy.cls.delete('VoiceActive');
    w.voice.measureHud = true;
    w.voice.caption = { id: 1, utterance: 9, text: 'Fall back!', translation: null, revision: 0 };
    assert.deepEqual(JSON.parse(JSON.stringify(packet().voiceShown)), [{ utterance: 9, stages: 1 }]);
    const caption = w.root.FindChildTraverse('DltVoiceCaption');
    assert.equal(caption.text, '队友语音  Fall back!');
    w.voice.caption = { ...w.voice.caption, translation: '撤', revision: 1 };
    assert.equal(packet().voiceShown[0].stages, 19);
    assert.equal(caption.text, '队友语音  Fall back!\n撤');
    w.voice.caption = { ...w.voice.caption, translation: '撤退！', revision: 2, final: true };
    assert.equal(packet().voiceShown[0].stages, 23);
    assert.equal(packet().voiceShown.length, 1, 'repeated rendering does not create extra receipts');
    assert.equal(caption.text, '队友语音  Fall back!\n撤退！');
    assert.equal(w.root.FindChildTraverse('DltVoiceCaption'), caption, 'successive chunks reuse the same label');
    assert.equal(caption.html, false);
    w.voice.caption = { id: 2, utterance: 10, text: 'Back away', translation: 'Retreat', revision: 0, final: true };
    w.voice.previous = { id: 1, utterance: 9, text: 'Do not push', translation: 'Hold', revision: 3,
        final: true, ageSeconds: 4, remainingMs: 120 };
    packet();
    const previous = w.root.FindChildTraverse('DltVoicePreviousCaption');
    assert.match(caption.text, /Back away\nRetreat$/);
    assert.match(previous.text, /4 .*Do not push/);
    w.holdBridge = true;
    w.step(7, 50);
    assert.equal(previous.visible, false, 'old line expires locally while the current line remains visible');
    assert.equal(caption.visible, true);
    delete w.voice.previous;
    w.holdBridge = true;
    w.step(14);
    assert.equal(caption.visible, false);
    w.holdBridge = false;
    delete w.voice.caption;
    w.destroy(local);
    w.step(60);
    assert.equal(packet().teammateSpeaking, null, 'unknown roster closes the gate');
});

test('voice test shows detection and translation stages before any subtitle exists', () => {
    const w = world();
    w.voice = { enabled: true, debug: { stage: 'voice-detected', speaking: true, device: 'vulkan', model: 'small-q5_1' } };
    w.step(18);
    const debug = w.root.FindChildTraverse('DltVoiceDebug');
    assert(debug.visible && debug.text.includes('检测到队友发言') && debug.text.includes('Vulkan'));
    assert.equal(w.root.FindChildTraverse('DltVoiceCaption'), null);
    w.voice.debug = { stage: 'voice-asr-start', device: 'vulkan', model: 'small-q5_1', audioMs: 810 };
    w.step(7);
    assert(debug.text.includes('正在识别队友语音') && debug.text.includes('0.81 秒'));
    w.voice.debug = { stage: 'voice-control-error', device: 'vulkan', model: 'small-q5_1' };
    w.step(7);
    assert(debug.text.includes('语音进程通信异常'));
    delete w.voice.debug;
    w.step(7);
    assert.equal(debug.visible, false);
});
