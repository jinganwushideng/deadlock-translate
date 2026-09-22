// SPDX-License-Identifier: GPL-3.0-only
// Panorama integration references BabelTower; see NOTICE and THIRD-PARTY-NOTICES.md.
// Separately maintained DeadlockTranslate implementation.
// Independent Panorama adapter. No OCR, process memory access or console-based chat sending.
// Pairing value is generated locally by the build; it is not a provider API key.
var DLTOpenSettings = function () {};
(function () {
    'use strict';
    try { $.Msg('[DLT] chat adapter 0.2.7 initializing'); } catch (_) {}
    var context = $.GetContextPanel();
    var ROOT = context;
    while (ROOT.GetParent && ROOT.GetParent()) ROOT = ROOT.GetParent();
    if (ROOT.__dltOwner && valid(ROOT.__dltOwner)) return;
    ROOT.__dltOwner = context;
    var base = 'http://localhost:17865/bridge/__PAIRING__';
    var state = { session: String(Date.now()) + '-' + String(Math.random()).slice(2), seq: 0, serial: 0,
        chat: null, chats: [], lobby: null, bridge: null, pending: null, records: [], outbox: [], render: [], hud: [],
        online: false, initialized: false, openSettings: false, nextConnect: 0, nextDiscovery: 0,
        nextPoll: 0, nextMetrics: 0, nextScan: 0, hotkeySeen: 0, outgoingQueue: [], outgoingSent: [], outgoingKnown: {},
        outgoingRefused: [],
        config: { enabled: true, translateOwn: false, fontSize: 17, scanIntervalMs: 200, transport: 'fragment',
            outgoingEnabled: true, outgoingTimeoutSeconds: 30, incomingScope: 'all_players', outgoingDisplayMode: 'translation' },
        chatTarget: { readable: false, panel: null, lastNative: '', lastLabel: '', changes: 0 },
        channelLog: [], echo: null, channelFault: '', backendDisplayMode: true, partyAvailable: false,
        pendingHotkey: null, closeWatch: null, forcePoll: false,
        metrics: { scans: 0, rows: 0, submitted: 0, rendered: 0, bridges: 0, errors: 0, maxScanMs: 0,
            regularRows: 0, hudRows: 0, lobbyRows: 0, skippedSelf: 0, skippedUnknown: 0, sendAttempts: 0, sendErrors: 0,
            channelWaits: 0, channelRetargets: 0, channelFaults: 0, hotkeyDrops: 0, classChannelReads: 0 } };
    function valid(panel) { try { return !!panel && (!panel.IsValid || panel.IsValid()); } catch (_) { return false; } }
    function find(panel, id) { try { return valid(panel) ? panel.FindChildTraverse(id) : null; } catch (_) { return null; } }
    function classes(panel, name) { try { return panel.FindChildrenWithClassTraverse(name) || []; } catch (_) { return []; } }
    function text(panel) { try { return valid(panel) ? String(panel.text || '') : ''; } catch (_) { return ''; } }
    function count(panel) { try { return valid(panel) ? panel.GetChildCount() : 0; } catch (_) { return 0; } }
    function child(panel, i) { try { return panel.GetChild(i); } catch (_) { return null; } }
    function has(panel, name) { try { return valid(panel) && panel.BHasClass(name); } catch (_) { return false; } }
    function normalized(value) { return String(value || '').replace(/\s+/g, ' ').trim(); }
    function contentText(panel) {
        var stack = [panel], result = [], visited = 0;
        while (stack.length && visited++ < 32) {
            var item = stack.pop();
            if (!valid(item) || has(item, 'DltTranslation') || has(item, 'DltBubbleTranslation')) continue;
            var value = text(item);
            if (value) { result.push(value); continue; }
            for (var i = Math.min(count(item), 16) - 1; i >= 0; i--) stack.push(child(item, i));
        }
        return normalized(result.join(' '));
    }
    function channelOf(value) {
        value = normalized(value).toLowerCase();
        if (/\bparty\b|队伍|隊伍|小队|小隊|组队|組隊|группа/.test(value)) return 'party';
        if (/allies|\bally\b|\bteam\b|友军|友軍|盟友|команда|союзник/.test(value)) return 'team';
        if (/\ball\b|全部|所有人|全体/.test(value)) return 'all';
        return '';
    }
    function stockSubmit() {
        return find(context, 'ChatLinesPanel') && !find(context, 'ChatMessages') ? 'CitadelChatTextSubmitted' : 'CitadelChatInputSubmitted';
    }
    function titleEvent() {
        if (!valid(context)) return;
        for (var i = 0; i < arguments.length; i++) {
            var candidate = arguments[i];
            if (typeof candidate === 'string' && candidate.indexOf('DLT:') === 0) { state.eventTitle = candidate; return; }
        }
    }
    try { $.RegisterForUnhandledEvent('HTMLChangedTitle', titleEvent); } catch (_) {}
    try { $.RegisterForUnhandledEvent('HTMLTitle', titleEvent); } catch (_) {}
    function status(value) { state.lastStatus = value; }

    // ---- 发送频道：读取并恢复原生目标 --------------------------------------
    // Current CitadelChat caches the target when opened and exposes it using the native
    // chat_target CSS class group. Its submit handler uses that same cached target after
    // blur. chat_target is NOT a documented JS property. Read engine-owned classes first;
    // never write a CSS class to change the actual recipient. Symbolic property handling
    // remains a fallback only when a panel really exposes a recognised native value.
    function targetFromNative(value) {
        var raw = String(value === undefined || value === null ? '' : value).toLowerCase();
        if (raw.indexOf('gameallies') >= 0) return 'team';
        if (raw.indexOf('gameall') >= 0) return 'all';
        if (raw.indexOf('party') >= 0) return 'party';
        return '';
    }
    function targetPanels() {
        var list = [];
        if (valid(context)) list.push(context);
        if (valid(state.rootChat) && state.rootChat !== context) list.push(state.rootChat);
        return list;
    }
    function nativeChannel() {
        // Capability evidence expires with the read. Never reuse a panel cached before a
        // rebuild or turn an absent native property into a writable JS expando.
        state.chatTarget.readable = false;
        state.chatTarget.panel = null;
        var list = targetPanels();
        for (var c = 0; c < list.length; c++) {
            var team = has(list[c], 'ChatTarget_GameAllies'), all = has(list[c], 'ChatTarget_GameAll'), party = has(list[c], 'ChatTarget_Party');
            var matches = (team ? 1 : 0) + (all ? 1 : 0) + (party ? 1 : 0);
            if (matches > 1) return ''; // Contradictory native evidence cannot authorize a send.
            if (matches === 1) {
                state.metrics.classChannelReads++;
                return team ? 'team' : all ? 'all' : 'party';
            }
        }
        for (var i = 0; i < list.length; i++) {
            try {
                var raw = list[i].chat_target;
                if (raw === undefined || raw === null) continue;
                var name = targetFromNative(raw);
                if (name) { state.chatTarget.panel = list[i]; state.chatTarget.readable = true; return name; }
            } catch (_) {}
        }
        return '';
    }
    function writeNativeChannel(channel) {
        // Never assign unless the property already returned a recognised value: writing to a plain
        // JS expando would read back exactly what we wrote while the game ignores it completely.
        if (!state.chatTarget.readable) return false;
        var value = channel === 'team' ? 'ChatTarget_GameAllies' : channel === 'all' ? 'ChatTarget_GameAll' : channel === 'party' ? 'ChatTarget_Party' : '';
        if (!value) return false;
        var panel = state.chatTarget.panel || context;
        try { panel.chat_target = value; state.metrics.channelRetargets++; return true; } catch (_) { return false; }
    }
    function labelChannel() { return channelOf(text(state.targetLabel)); }
    function noteChannel(kind, detail) {
        detail = detail || {};
        var entry = { at: Date.now(), kind: kind, native: detail.native || '', label: detail.label || '',
            channel: detail.channel || '', result: detail.result || '' };
        if (state.channelLog.length >= 12) state.channelLog.shift();
        state.channelLog.push(entry);
        state.lastChannel = entry;
        return entry;
    }
    function logChannel(message) {
        if (state.lastChannelLog === message) return;
        state.lastChannelLog = message;
        try { $.Msg('[DLT] channel: ' + message); } catch (_) {}
    }
    function channelNow() {
        var native = nativeChannel(), label = labelChannel();
        if (native !== state.chatTarget.lastNative || label !== state.chatTarget.lastLabel) {
            state.chatTarget.lastNative = native;
            state.chatTarget.lastLabel = label;
            state.chatTarget.changes++;
            noteChannel('changed', { native: native, label: label });
        }
        return { native: native, label: label };
    }
    function channelConfirmed(desired) {
        var now = channelNow();
        // A label is useful for capturing user intent, never for authorizing a send.
        return (desired === 'team' || desired === 'all' || desired === 'party') && now.native === desired;
    }
    function ensureChannel(job, desired) {
        if (channelConfirmed(desired)) { job.retryAt = 0; return 'ok'; }
        if (!state.chatTarget.readable) return 'unsupported';
        if (job.retargets >= 2) return 'unconfirmed';
        if (job.retryAt && Date.now() - job.retryAt < 250) return 'pending';
        var before = channelNow();
        if (!job.before) job.before = before.native || before.label;
        job.retryAt = Date.now();
        job.retargets++;
        var wrote = writeNativeChannel(desired);
        noteChannel('retarget', { native: before.native, label: before.label, channel: desired, result: wrote ? 'written' : 'unavailable' });
        if (!wrote) { job.retargets = 2; logChannel('no writable native channel; task kept unsent'); return 'unconfirmed'; }
        logChannel('retarget to ' + desired + ' (attempt ' + job.retargets + ')');
        return 'pending';
    }
    function restoreChannel(job) {
        // Put back the target we found after the close sequence, so our temporary redirection
        // never leaves the player's chat controller changed.
        if (job.restored || !job.before) return;
        job.restored = true;
        if (writeNativeChannel(job.before)) noteChannel('restore', { channel: job.before, result: 'restored' });
    }
    function restoreAfterClose(channel, id) {
        // Remember what the native target became after the close sequence: that is the state we
        // must put back once the send is done.
        var job = id ? state.outgoingKnown[id] : null;
        var afterClose = channelNow();
        if (job && !job.before && state.chatTarget.readable) job.before = afterClose.native || '';
        if (channelConfirmed(channel) || !state.chatTarget.readable) return;
        var wrote = writeNativeChannel(channel);
        noteChannel('retarget-after-close', { native: afterClose.native, label: afterClose.label, channel: channel, result: wrote ? 'written' : 'unavailable' });
        if (wrote) logChannel('channel restored right after closing the input: ' + channel);
    }
    function inputOpen() {
        try {
            var input = state.input;
            return !!input && context.BHasClass('ChatExpanded') && (!input.BHasKeyFocus || input.BHasKeyFocus());
        } catch (_) { return false; }
    }
    function outgoingDisplayMode() {
        if (!state.backendDisplayMode) return 'translation';
        return state.config.outgoingDisplayMode === 'bilingual' ? 'bilingual' : 'translation';
    }
    function hotkeyOutcome(reason) {
        if (!state.pendingHotkey) return;
        state.hotkeySeen = state.pendingHotkey.seq;
        state.pendingHotkey = null;
        noteChannel('hotkey', { result: reason });
        if (reason !== 'captured') logChannel('hotkey ignored: ' + reason);
    }
    function captureOutgoing() {
        var held = state.pendingHotkey;
        if (!state.config.outgoingEnabled) { hotkeyOutcome('disabled'); return; }
        if (!inputOpen()) {
            // The reported chat focus can lag the keypress by a bridge round trip. Keep the chord
            // for a short bounded window and retry instead of losing it silently.
            if (held && Date.now() - held.at > 2500) { state.metrics.hotkeyDrops++; hotkeyOutcome('input-not-open'); }
            return;
        }
        var input = state.input, value = text(input).trim();
        if (!value) { hotkeyOutcome('empty'); return; }
        var capacity = Math.max(3, Math.min(32, Number(state.config.outgoingQueueLimit) || 3));
        var activeOutgoing = Object.keys(state.outgoingKnown).filter(function (id) { return !state.outgoingKnown[id].done; }).length;
        if (activeOutgoing >= capacity) { state.metrics.hotkeyDrops++; hotkeyOutcome('queue-full'); return; }
        var nativeName = nativeChannel();
        var labelName = labelChannel();
        // The native value is the truth; the label is the player's visible choice and covers older
        // builds without a readable property. PARTY is a distinct recipient, never an alias of ALLIES.
        var channel = nativeName || labelName;
        if (channel === 'party' && !state.partyAvailable) {
            state.metrics.hotkeyDrops++; hotkeyOutcome('backend-outdated-party');
            logChannel('party translation requires a matching backend; draft preserved'); return;
        }
        if (!channel) {
            state.metrics.hotkeyDrops++;
            noteChannel('capture-unsupported', { native: nativeName, label: labelName });
            logChannel('capture: unsupported channel; draft preserved');
            hotkeyOutcome('unsupported-channel');
            return;
        }
        var id = 'out-' + String(++state.serial);
        state.outgoingQueue.push({ id: id, text: value, channel: channel, displayMode: outgoingDisplayMode() });
        state.outgoingKnown[id] = { created: Date.now(), attempted: false, done: false, waiting: false, retargets: 0,
            retryAt: 0, before: '', restored: false };
        noteChannel('capture', { native: nativeName, label: labelName, channel: channel });
        input.text = '';
        // Empty native submit returns control exactly as normal chat submission does.
        $.DispatchEvent(stockSubmit(), input);
        $.DispatchEvent('CitadelChatInputBlur', input);
        // The empty submit occasionally leaves the box expanded. Watch the collapse, retry the
        // native close while the box is still empty, then put the captured channel back.
        state.closeWatch = { id: id, channel: channel, at: Date.now(), attempts: 0 };
        hotkeyOutcome('captured');
    }
    function watchClose() {
        var watch = state.closeWatch;
        if (!watch) return;
        var now = Date.now(), elapsed = now - watch.at, input = state.input;
        if (!has(context, 'ChatExpanded')) {
            state.closeWatch = null;
            noteChannel('close', { result: 'closed' });
            restoreAfterClose(watch.channel, watch.id);
            return;
        }
        if (text(input) !== '') {
            // The player is editing again; never submit or close their new input.
            state.closeWatch = null;
            noteChannel('close', { result: 'player-typing' });
            return;
        }
        if (elapsed > 600 || watch.attempts >= 2 || elapsed < 150 || !valid(input)) {
            if (elapsed > 600) { state.closeWatch = null; state.metrics.hotkeyDrops++; noteChannel('close', { result: 'still-open' }); }
            return;
        }
        watch.attempts++;
        watch.at = now;
        $.DispatchEvent(stockSubmit(), input);
        $.DispatchEvent('CitadelChatInputBlur', input);
        noteChannel('close', { result: 'retry-' + watch.attempts });
        logChannel('chat box stayed open; retried the native close (' + watch.attempts + ')');
    }
    function sendPayload(result) {
        var mode = result.displayMode === 'bilingual' ? 'bilingual' : 'translation';
        var value = mode === 'bilingual' ? result.sendText : (result.sendText || result.english);
        return { mode: mode, value: String(value === undefined || value === null ? '' : value) };
    }
    function processOutgoing(results) {
        for (var i = 0; i < results.length; i++) {
            var result = results[i], job = state.outgoingKnown[result.id];
            if (!job || job.done) continue;
            if (result.state === 'failed') { job.done = true; continue; }
            if (!state.config.outgoingEnabled) { refuseOutgoing(result.id, job, 'disabled'); continue; }
            if ((result.state !== 'ready' && result.state !== 'waiting_channel') || job.attempted) continue;
            if (Date.now() - job.created > (state.config.outgoingTimeoutSeconds + 5) * 1000) { job.done = true; continue; }
            if (result.channel !== 'team' && result.channel !== 'all' && result.channel !== 'party') { job.done = true; continue; }
            var payload = sendPayload(result);
            if (payload.mode === 'bilingual' && !result.sendText) {
                // Do not silently send English only when the user asked for 原文 + 英文.
                state.metrics.sendErrors++;
                logChannel('backend has no SendText for a bilingual task; refused instead of sending English only');
                refuseOutgoing(result.id, job, 'backend-outdated');
                continue;
            }
            // Same single-line plain text rules as the backend, applied a second time here.
            if (!payload.value || payload.value.length > 500 || /[\x00-\x1f\x7f";\\]/.test(payload.value)) {
                state.metrics.sendErrors++;
                refuseOutgoing(result.id, job, 'invalid-send-text');
                continue;
            }
            // The native input is also the user's editor. Defer while they are typing;
            // never close a newly opened chat or change the current chat channel.
            if (!valid(state.input) || inputOpen() || has(context, 'ChatExpanded')) continue;
            if (state.channelFault) {
                if (!job.waiting) { job.waiting = true; state.metrics.channelWaits++; }
                logChannel('held: an earlier message appeared in a different channel (' + state.channelFault + ')');
                continue;
            }
            var verdict = ensureChannel(job, result.channel);
            if (verdict === 'pending') continue;
            if (verdict !== 'ok') {
                if (!job.waiting) { job.waiting = true; state.metrics.channelWaits++; }
                job.channelWaitReason = verdict;
                continue;
            }
            job.waiting = false;
            job.attempted = true; // Never repeat a command after an uncertain dispatch.
            var draft = text(state.input);
            try {
                state.input.text = payload.value;
                var accepted = $.DispatchEvent(stockSubmit(), state.input);
                state.metrics.sendAttempts++;
                if (accepted !== false) {
                    state.outgoingSent.push(result.id);
                    state.echo = { text: payload.value, channel: result.channel, at: Date.now(), checked: false };
                    noteChannel('submit', { channel: result.channel, result: 'dispatched' });
                } else { state.metrics.sendErrors++; noteChannel('submit', { channel: result.channel, result: 'rejected' }); refuseOutgoing(result.id, job, 'submit-rejected'); }
                job.done = true;
            } catch (_) { job.done = true; state.metrics.errors++; state.metrics.sendErrors++; refuseOutgoing(result.id, job, 'submit-error'); }
            finally {
                if (valid(state.input)) state.input.text = draft;
                restoreChannel(job);
            }
        }
    }
    // Bounded echo observation: only the last submitted text, only for a few seconds, and only a
    // definite channel marker counts. It cannot unsend anything, but a mismatch stops further
    // sends instead of repeating a public message.
    function checkEcho(value, channel, role, freshText) {
        var echo = state.echo;
        if (!echo || echo.checked || Date.now() - echo.at > 8000) return;
        if (!freshText || role !== 'self' || (channel !== 'team' && channel !== 'all' && channel !== 'party')) return;
        if (value !== echo.text) return;
        echo.checked = true;
        if (channel === echo.channel) { noteChannel('echo', { channel: channel, result: 'confirmed' }); return; }
        state.channelFault = echo.channel + '->' + channel;
        state.metrics.channelFaults++;
        noteChannel('echo', { channel: channel, result: 'mismatch' });
        logChannel('a ' + echo.channel + ' message appeared in ' + channel + '; further sends are held');
    }
    function refuseOutgoing(id, job, reason) {
        if (job.refused) return;
        job.refused = true; job.done = true;
        state.outgoingRefused.push({ id: id, reason: reason });
        while (state.outgoingRefused.length > 12) state.outgoingRefused.shift();
    }
    function localTeam() {
        try {
            var detected = 0;
            if (typeof Players !== 'undefined' && Players.GetLocalPlayer && Players.GetTeam) detected = Number(Players.GetTeam(Players.GetLocalPlayer()));
            if (detected === 2 || detected === 3) return detected;
            if (typeof Game !== 'undefined' && Game.GetLocalPlayerInfo) detected = Number(Game.GetLocalPlayerInfo().player_team_id);
            if (detected === 2 || detected === 3) return detected;
        } catch (_) {}
        var panels = [find(ROOT, 'TeamFriendly'), find(ROOT, 'TeamEnemy')];
        for (var p = 0; p < panels.length; p++) {
            var teamPanel = panels[p];
            if (!valid(teamPanel)) continue;
            if (has(teamPanel, 'friend') || classes(teamPanel, 'LocalPlayer').length) {
                if (has(teamPanel, 'team1')) return 2;
                if (has(teamPanel, 'team2')) return 3;
            }
        }
        return 0;
    }
    function rowRole(row, kind, team) {
        if (has(row, 'IsSelf') || classes(row, 'SenderLocalClient').length) return 'self';
        if (has(row, 'IsEnemy') || has(row, 'enemy')) return 'enemy';
        var channel = text(classes(row, kind === 'lobby' ? 'ChatLinePrefix' : 'ChannelName')[0]);
        if (channelOf(channel) === 'team' || channelOf(channel) === 'party') return 'peer';
        if (has(row, 'friend') || has(row, 'IsFriendly') || has(row, 'IsAlly')) return 'peer';
        if (kind === 'hud' && state.team && team) return team === state.team ? 'peer' : 'enemy';
        return 'unknown';
    }
    function messageChannel(row, kind) {
        if (kind === 'hud') return 'hud';
        if (kind === 'lobby') return channelOf(text(classes(row, 'ChatLinePrefix')[0])) || 'lobby';
        return channelOf(text(classes(row, 'ChannelName')[0]));
    }
    function rowText(row, kind) {
        if (kind === 'hud') return normalized(text(find(row, 'MessageText')));
        if (kind === 'lobby') return contentText(classes(row, 'ChatLine')[0]);
        var contents = find(row, 'MessageContents');
        return contentText(contents);
    }
    function shouldTranslate(value) {
        if (!value || value.length > 1024) return false;
        // Chinese + numbers/punctuation has no translation work. Kana and other scripts still translate.
        return /[A-Za-z\u0400-\u04ff\u3040-\u30ff\uac00-\ud7af\u0600-\u06ff\u00c0-\u024f\u0e00-\u0e7f]/.test(value);
    }
    function roleAllowed(role) {
        // all_players is the shipped default: teammates, enemies, the local player and messages
        // whose side is unknown all translate. teammates keeps the old conservative filter.
        if (state.config.incomingScope === 'teammates') return role === 'peer';
        return role === 'peer' || role === 'self' || role === 'enemy' || role === 'unknown';
    }
    function rolesCompatible(a, b) { return a === b || a === 'unknown' || b === 'unknown'; }
    function realChannel(value) { return value === 'team' || value === 'all' ? value : ''; }
    function observe(row, kind, team) {
        if (!valid(row)) return;
        state.metrics.rows++; state.metrics[kind + 'Rows']++;
        var contents = find(row, 'MessageContents');
        var quick = has(row, 'Ping') || has(contents, 'Ping') || !!find(row, 'PingLabel');
        var value = rowText(row, kind), role = rowRole(row, kind, team), channel = messageChannel(row, kind);
        var sender = text(classes(row, kind === 'lobby' ? 'ChatPersona' : 'SenderName')[0]).slice(0, 100);
        var freshText = value !== row.__dltEchoObservedText;
        row.__dltEchoObservedText = value;
        checkEcho(value, channel, role, freshText);
        var scope = state.config.incomingScope === 'teammates' ? 'teammates' : 'all_players';
        var fingerprint = value + '\n' + sender + '\n' + role + '\n' + channel + '\n' + String(quick) + '\n' + scope;
        if (row.__dltFingerprint === fingerprint && row.__dltSession === state.session) return;
        if (valid(row.__dltLabel)) row.__dltLabel.text = '';
        row.__dltFingerprint = fingerprint; row.__dltSession = state.session;
        if (role === 'self' && !roleAllowed(role)) state.metrics.skippedSelf++;
        if (role === 'unknown' && !roleAllowed(role)) state.metrics.skippedUnknown++;
        if (!state.config.enabled || quick || !shouldTranslate(value) || !roleAllowed(role)) return;
        var record = null;
        for (var i = state.records.length - 1; i >= 0; i--) {
            var candidate = state.records[i];
            if (Date.now() - candidate.created > 2500) break;
            if (candidate.original !== value) continue;
            if (!rolesCompatible(candidate.role, role)) continue;
            if (candidate.sender && sender && candidate.sender !== sender) continue;
            // Views without a channel marker (HUD bubbles, lobby) stay compatible; a definite
            // team/all mismatch must not be merged into one translation.
            if (realChannel(candidate.channel) && realChannel(channel) && realChannel(candidate.channel) !== realChannel(channel)) continue;
            // The same view never merges twice; a third ambiguous hit is treated as a new message.
            if (candidate.views.some(function (v) { return v.kind === kind; })) continue;
            if (record) { record = null; break; }
            record = candidate;
        }
        if (!record) {
            record = { id: String(++state.serial), original: value, sender: sender, role: role, channel: channel,
                created: Date.now(), revision: 0, done: false, translation: '', stage: '', views: [] };
            state.records.push(record);
            if (state.outbox.length < 64) state.outbox.push({ id: record.id, text: value, sender: sender, channel: channel });
            else { record.done = true; state.metrics.errors++; }
        }
        record.views.push({ row: row, kind: kind, fingerprint: fingerprint });
        if (record.translation) queueRender(record);
    }
    function scanContainer(container, kind, team, limit) {
        var n = count(container);
        for (var i = Math.max(0, n - limit); i < n; i++) {
            try { observe(child(container, i), kind, team); } catch (_) { state.metrics.errors++; }
        }
    }
    function scanChat() {
        for (var i = 0; i < state.chats.length; i++) scanContainer(state.chats[i], 'regular', 0, 24);
        scanContainer(state.lobby, 'lobby', 0, 24);
    }
    function discover() {
        if (!valid(state.chat)) state.chat = find(context, 'ChatMessages');
        if (!valid(state.rootChat)) state.rootChat = find(ROOT, 'Chat');
        if (!valid(state.rootMessages)) state.rootMessages = find(state.rootChat, 'ChatMessages') || find(ROOT, 'ChatMessages');
        state.chats = [];
        var candidates = [state.chat, state.rootMessages];
        for (var c = 0; c < candidates.length; c++) if (valid(candidates[c]) && state.chats.indexOf(candidates[c]) < 0) state.chats.push(candidates[c]);
        if (!valid(state.lobby)) state.lobby = find(ROOT, 'ChatLinesPanel');
        if (!valid(state.bridge)) state.bridge = find(context, 'DltBridge');
        if (valid(state.bridge) && !state.bridgeEventsBound) {
            state.bridgeEventsBound = true;
            try { $.RegisterEventHandler('HTMLChangedTitle', state.bridge, titleEvent); } catch (_) {}
            try { $.RegisterEventHandler('HTMLTitle', state.bridge, titleEvent); } catch (_) {}
        }
        if (!valid(state.input)) state.input = find(context, 'ChatInput');
        if (!valid(state.targetLabel)) state.targetLabel = find(context, 'ChatTargetLabel');
        // Searching the full root is limited to infrequent discovery, never the normal scan loop.
        var team = localTeam();
        state.team = team; state.hud = []; state.hudRoots = state.hudRoots || [];
        var hudIds = ['Team1Chat', 'Team2Chat'];
        for (var h = 0; h < hudIds.length; h++) {
            if (!valid(state.hudRoots[h])) state.hudRoots[h] = find(ROOT, hudIds[h]);
            var panel = state.hudRoots[h], messages = find(panel, 'Messages');
            if (valid(messages)) state.hud.push({ panel: messages, team: h + 2 });
        }
        try {
            if (!valid(state.menuMount)) {
                var settings = find(ROOT, 'SubOptions');
                if (valid(settings)) {
                    state.menuMount = find(settings, 'DltMenuMount');
                    if (!valid(state.menuMount)) {
                        var mount = $.CreatePanel('Panel', settings, 'DltMenuMount');
                        mount.style.width = '100%';
                        mount.BLoadLayout('file://{resources}/layout/dlt_menu.xml', false, false);
                        var quit = find(settings, 'quit');
                        if (quit && settings.MoveChildBefore) settings.MoveChildBefore(mount, quit);
                        state.menuMount = mount;
                    }
                }
            }
        } catch (_) { state.metrics.errors++; }
        state.nextDiscovery = Date.now() + (state.hud.length ? 10000 : 2000);
    }
    function scanBubbles() {
        for (var h = 0; h < state.hud.length; h++) scanContainer(state.hud[h].panel, 'hud', state.hud[h].team, 12);
    }
    function queueRender(record) { if (state.render.indexOf(record) < 0) state.render.push(record); }
    function updateLabel(label, value) { if (label.text !== value) label.text = value; label.style.fontSize = state.config.fontSize + 'px'; }
    function renderOne(record) {
        var value = record.translation; if (!value) return;
        for (var i = 0; i < record.views.length; i++) {
            var view = record.views[i], row = view.row;
            if (!valid(row) || row.__dltFingerprint !== view.fingerprint) continue;
            var host;
            if (view.kind === 'hud') host = find(row, 'MessageContents') || row;
            else if (view.kind === 'lobby') {
                var original = find(row, 'DltLobbyOriginal');
                if (!valid(original)) {
                    var children = [];
                    for (var c = 0; c < count(row); c++) if (!has(child(row, c), 'DltTranslation')) children.push(child(row, c));
                    original = $.CreatePanel('Panel', row, 'DltLobbyOriginal');
                    original.style.flowChildren = 'right'; original.style.width = '100%';
                    for (var m = 0; m < children.length; m++) children[m].SetParent(original);
                    row.style.flowChildren = 'down';
                }
                host = row;
            } else host = classes(row, 'MessageBody')[0] || row;
            if (!valid(host)) continue;
            var label = row.__dltLabel;
            if (!valid(label)) {
                label = $.CreatePanel('Label', host, view.kind === 'hud' ? 'DltBubbleTranslation' : 'DltTranslation');
                label.AddClass(view.kind === 'hud' ? 'DltBubbleTranslation' : 'DltTranslation');
                label.html = false; label.hittest = false; row.__dltLabel = label;
                // Visuals live in CSS so history and HUD share the game palette.
            }
            updateLabel(label, value);
        }
        state.metrics.rendered++;
    }
    function receive(payload) {
        if (payload.error) { state.metrics.errors++; try { $.Msg('[DLT] bridge response error (no message content logged)'); } catch (_) {} return; }
        if (!state.pending || payload.seq !== state.pending.seq) return;
        var accepted = state.pending.messages || [];
        for (var a = 0; a < accepted.length; a++) { for (var q = 0; q < state.outbox.length; q++) if (state.outbox[q].id === accepted[a].id) { state.outbox.splice(q, 1); break; } }
        state.metrics.submitted += accepted.length;
        var acceptedOutgoing = state.pending.outgoing || [];
        for (var o = 0; o < acceptedOutgoing.length; o++) state.outgoingQueue = state.outgoingQueue.filter(function (item) { return item.id !== acceptedOutgoing[o].id; });
        var acknowledged = state.pending.outgoingSent || [];
        state.outgoingSent = state.outgoingSent.filter(function (id) { return acknowledged.indexOf(id) < 0; });
        if (state.pending.refused) state.outgoingRefused = [];
        state.pending = null; state.online = true;
        if (state.config.transport === 'fragment' && !state.autoFallback) state.fragmentWorked = true;
        if (payload.serverId && state.serverId && payload.serverId !== state.serverId) {
            state.outgoingQueue = []; state.outgoingSent = []; state.outgoingKnown = {};
            state.pendingHotkey = null; state.closeWatch = null; state.outgoingRefused = [];
            state.echo = null; state.channelFault = '';
            state.hotkeySeen = payload.hotkey || 0;
            for (var s = 0; s < state.records.length; s++) {
                var retry = state.records[s];
                if (!retry.done && Date.now() - retry.created < 30000 && state.outbox.length < 64 && !state.outbox.some(function (m) { return m.id === retry.id; }))
                    state.outbox.push({ id: retry.id, text: retry.original, sender: retry.sender, channel: retry.channel });
            }
        }
        if (payload.serverId) state.serverId = payload.serverId;
        if (payload.config) state.config = payload.config;
        if (!state.config.incomingScope) state.config.incomingScope = 'all_players';
        var capabilities = payload.capabilities;
        state.partyAvailable = !!capabilities && capabilities.indexOf('outgoing-party-channel') >= 0;
        state.backendDisplayMode = typeof state.config.outgoingDisplayMode === 'string' &&
            (!capabilities || capabilities.indexOf('outgoing-display-mode') >= 0);
        if (!state.config.outgoingDisplayMode) state.config.outgoingDisplayMode = 'translation';
        if (state.autoFallback) state.config.transport = 'navigation';
        if (payload.hotkey && payload.hotkey > state.hotkeySeen) {
            // Do not mark the chord as seen yet: captureOutgoing retries while the sendable
            // outcome is still unknown, and only then advances hotkeySeen.
            if (!state.pendingHotkey || state.pendingHotkey.seq !== payload.hotkey)
                state.pendingHotkey = { seq: payload.hotkey, at: Date.now() };
            captureOutgoing();
        }
        processOutgoing(payload.outgoing || []);
        var results = payload.results || [];
        for (var i = 0; i < results.length; i++) {
            var result = results[i];
            for (var j = state.records.length - 1; j >= 0; j--) {
                var record = state.records[j]; if (record.id !== result.id) continue;
                record.done = result.done;
                if (result.revision > record.revision && result.text) {
                    record.revision = result.revision; record.translation = result.text; record.stage = result.stage; queueRender(record);
                }
                break;
            }
        }
        status(state.config.enabled ? (payload.configuredLlm ? '翻译已连接' : '快译已连接 · 精翻未配置') : '翻译已暂停');
    }
    function bridgeTick() {
        var now = Date.now();
        if (!valid(state.bridge)) return;
        var title = state.eventTitle || String(state.bridge.title || '');
        if (!title && state.bridge.GetTitle) { try { title = String(state.bridge.GetTitle() || ''); } catch (_) {} }
        if (!title && state.bridge.GetAttributeString) { try { title = String(state.bridge.GetAttributeString('title', '') || ''); } catch (_) {} }
        if (title.indexOf('DLT:') === 0 && title !== state.lastTitle) {
            state.lastTitle = title;
            if (title === 'DLT:ready') state.initialized = true;
            else { try { receive(JSON.parse(title.slice(4))); state.initialized = true; } catch (_) { state.metrics.errors++; } }
        }
        if (state.pending && now - state.pending.at > 4500) {
            if (state.config.transport === 'fragment' && !state.fragmentWorked) { state.config.transport = 'navigation'; state.autoFallback = true; }
            state.metrics.errors++; state.pending = null; state.initialized = false; state.online = false;
            state.nextConnect = now + 5000; status('后台未连接 · 请运行启动器');
        }
        if (state.pending || now < state.nextConnect) return;
        if (!state.initialized) {
            state.lastTitle = '';
            state.eventTitle = '';
            if (!state.loggedBridge) { state.loggedBridge = true; try { $.Msg('[DLT] loading localhost bridge; GetTitle=' + String(typeof state.bridge.GetTitle)); } catch (_) {} }
            state.bridge.SetURL(base); state.metrics.bridges++; state.nextConnect = now + 5000;
            // initial load has a timeout as well; do not busy-loop when localhost is offline
            return;
        }
        var ids = [];
        for (var i = 0; i < state.records.length && ids.length < 48; i++) if (!state.records[i].done) {
            if (now - state.records[i].created > 90000) state.records[i].done = true;
            else ids.push(state.records[i].id);
        }
        var wantMetrics = now >= state.nextMetrics;
        var chatOpen = inputOpen();
        var outgoingPending = Object.keys(state.outgoingKnown).some(function (id) { return !state.outgoingKnown[id].done; });
        var waiting = [];
        for (var k in state.outgoingKnown) if (state.outgoingKnown[k].waiting && !state.outgoingKnown[k].done) waiting.push(k);
        if (!state.outbox.length && !state.outgoingQueue.length && !state.outgoingSent.length && !state.outgoingRefused.length && !state.openSettings && !wantMetrics && !state.forcePoll && (!(ids.length || chatOpen || outgoingPending) || now < state.nextPoll)) return;
        state.forcePoll = false;
        var messages = state.outbox.slice(0, 8);
        var payload = { session: state.session, seq: ++state.seq, messages: messages, poll: ids,
            openSettings: state.openSettings, metrics: wantMetrics ? state.metrics : null,
            chatOpen: chatOpen, hotkeySeen: state.hotkeySeen, outgoing: state.outgoingQueue.slice(0, 3),
            outgoingSent: state.outgoingSent.slice(0, 12), outgoingWaiting: waiting.slice(0, 12),
            outgoingRefused: state.outgoingRefused.slice(0, 12) };
        var encoded = encodeURIComponent(JSON.stringify(payload));
        while (encoded.length > 6000 && payload.messages.length > 1) { payload.messages.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        while (encoded.length > 6000 && payload.outgoing.length > 1) { payload.outgoing.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        if (encoded.length > 6000 && payload.outgoing.length && payload.messages.length) { payload.messages = []; encoded = encodeURIComponent(JSON.stringify(payload)); }
        state.pending = { seq: payload.seq, at: now, messages: payload.messages, outgoing: payload.outgoing, outgoingSent: payload.outgoingSent, refused: payload.outgoingRefused.length > 0 };
        state.openSettings = false; state.nextPoll = now + (chatOpen ? 80 : state.config.transport === 'navigation' ? 800 : 250);
        if (wantMetrics) state.nextMetrics = now + 10000;
        state.bridge.SetURL(base + (state.config.transport === 'navigation' ? '?d=' : '#') + encoded);
        state.metrics.bridges++;
    }
    DLTOpenSettings = function () { state.openSettings = true; state.nextConnect = 0; if (!state.initialized) status('正在连接后台 · 未启动时请运行 Settings.cmd'); };
    function checkSession() {
        var identity = '';
        try { if (typeof Game !== 'undefined' && Game.GetMatchID) { var match = String(Game.GetMatchID()); if (/^[1-9][0-9]*$/.test(match)) identity = match; } } catch (_) {}
        var newChat = find(context, 'ChatMessages');
        var changed = state.chat && valid(newChat) && newChat !== state.chat;
        // Empty chat history is not a new match; native UI may expire or clear its rows.
        if (identity && state.matchIdentity && identity !== state.matchIdentity) changed = true;
        if (changed) {
            state.session = String(Date.now()) + '-' + String(Math.random()).slice(2);
            state.records = []; state.outbox = []; state.render = []; state.pending = null;
            state.outgoingQueue = []; state.outgoingSent = []; state.outgoingKnown = {}; state.hotkeySeen = 0;
            state.echo = null; state.channelFault = ''; state.outgoingRefused = [];
            state.pendingHotkey = null; state.closeWatch = null; state.forcePoll = false;
            state.chat = newChat; state.initialized = false; state.nextConnect = 0;
        }
        if (identity) state.matchIdentity = identity;
    }
    function tick() {
        if (!valid(context)) return;
        var started = Date.now();
        try {
            if (started >= state.nextDiscovery) { checkSession(); discover(); }
            if (started >= state.nextScan) { scanChat(); scanBubbles(); state.nextScan = started + state.config.scanIntervalMs; }
            // A chord whose chat box was not open yet is retried here until its window expires.
            if (state.pendingHotkey) captureOutgoing();
            watchClose();
            var open = inputOpen();
            if (open !== state.lastOpenReported) { state.lastOpenReported = open; state.forcePoll = true; }
            // At most two records update in one scheduled callback. No streaming or animations.
            for (var r = 0; r < 2 && state.render.length; r++) renderOne(state.render.shift());
            bridgeTick();
            while (state.records.length > 300) state.records.shift();
        } catch (error) { state.metrics.errors++; if (!state.lastErrorAt || started - state.lastErrorAt > 10000) { state.lastErrorAt = started; try { $.Msg('[DLT] adapter error: ' + String(error)); } catch (_) {} } }
        state.metrics.scans++; state.metrics.maxScanMs = Math.max(state.metrics.maxScanMs, Date.now() - started);
        $.Schedule(inputOpen() ? 0.05 : Math.max(0.1, Math.min(1, state.config.scanIntervalMs / 1000)), tick);
    }
    $.Schedule(0.3, tick);
}());
