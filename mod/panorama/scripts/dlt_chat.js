// SPDX-License-Identifier: GPL-3.0-only
// Panorama integration references BabelTower; see NOTICE and THIRD-PARTY-NOTICES.md.
// Separately maintained DeadlockTranslate implementation.
// Independent Panorama adapter. No OCR, process memory access or console-based chat sending.
// Pairing value is generated locally by the build; it is not a provider API key.
var BUILD_ID = '0.7.0-9e3c5769bead';
var DLTOpenSettings = function () {};
var DLTChatInputFocused = function () {};
(function initialize(restartReason) {
    'use strict';
    var context = $.GetContextPanel();
    if (!valid(context)) return;
    var ROOT = rootOf(context); if (!valid(ROOT)) return;
    if (ROOT.__dltOwner && valid(ROOT.__dltOwner)) {
        // Some scene switches create the replacement chat before deleting the old one.
        // A standby instance has no global handlers, browser navigation or chat scanning.
        if (ROOT.__dltOwner !== context) $.Schedule(0.5, function () { if (valid(context) && valid(ROOT)) initialize(restartReason); });
        return;
    }
    ROOT.__dltOwner = context;
    try { $.Msg('[DLT] chat adapter 0.6.25 HTTPS initializing'); } catch (_) {}
    var stopped = false, scheduled = null, closeGuard = null, globalHandlers = [], boundBridge = null, loopGeneration = 0, lastTickAt = Date.now(), lastResumeAt = 0;
    var base = 'https://localhost:17866/bridge/__PAIRING__';
    var loopbackHost = 'localhost', initialLoads = 0;
    var heroLabels = {}; // __DLT_HERO_LABELS__
    var state = { session: String(Date.now()) + '-' + String(Math.random()).slice(2), seq: 0, serial: 0,
        chat: null, chats: [], lobby: null, bridge: null, pending: null, records: [], recordsById: Object.create(null), outbox: [], render: [], hud: [],
        online: false, initialized: false, openSettings: false, nextConnect: 0, nextDiscovery: 0, nextMenuProbe: 0, menuProbeMisses: 0, discoveryWalk: null, discoveryIndex: {}, performanceBuckets: [], nextSceneCheck: 0, resumeAt: 0, nextBridgeCreate: 0, titleSamples: [], lastReplyAt: 0,
        nextHeartbeat: 0, preview: null, previewRoot: null, nextPoll: 0, nextNavigation: 0, nextMetrics: 0, nextScan: 0, hotkeySeen: 0, outgoingQueue: [], outgoingSent: [], outgoingPartAcks: [], nextSendAt: 0, outgoingKnown: {},
        outgoingRefused: [], recentOwn: [], feed: null, feedSlots: [], feedChecks: [], feedMode: 'native', showOwnDisplay: true, nextFeedCreate: 0, hudRetries: [], nativeChecks: [], nativeLocalSerial: 0, extraHudRoots: [], hudSearch: null, pollCursor: 0, hudPollCursor: 0,
        config: { enabled: true, translateOwn: false, fontSize: 17, scanIntervalMs: 200, transport: 'fragment',
            incomingDisplayMode: 'native', incomingShowOwnTranslations: true, incomingFeedSeconds: 8, incomingFeedLines: 3, incomingShowHeroName: true, incomingShowAccountName: true, incomingHeroNamePosition: 'before', incomingOpacity: 90, incomingFeedWidth: 340, incomingFeedPosition: 'center-left', incomingFeedOffsetX: 28, incomingFeedOffsetY: 0,
            outgoingEnabled: true, outgoingTimeoutSeconds: 30, incomingScope: 'all_players', outgoingDisplayMode: 'translation', incomingTargetLanguage: 'zh-Hans', outgoingTargetLanguage: 'en' },
        chatTarget: { readable: false, panel: null, source: '', evidencePanel: null, conflict: false, epoch: 0, lastNative: '', lastLabel: '', changes: 0 },
        channelLog: [], echoes: [], outgoingStatus: [], statusSerial: 0, statusSupported: false, turn: 0, channelFault: '', backendDisplayMode: true, partyAvailable: false, multiLanguageAvailable: false,
        pendingHotkey: null, closeWatch: null, nativeSettleUntil: 0, nativeClosedAt: 0, outgoingOrdinal: 0, forcePoll: false, logs: [], logSeq: 0, diagSupported: false,
        heroRoster: [], heroTeams: [], nextHeroRoster: 0, heroRosterCount: -1,
        metrics: { scans: 0, rows: 0, submitted: 0, rendered: 0, bridges: 0, errors: 0, maxScanMs: 0,
            regularRows: 0, hudRows: 0, hudContainers: 0, hudRebinds: 0, hudRendered: 0, hudRecovered: 0, hudHostRepairs: 0, hudHiddenRepairs: 0, hudBindings: 0, hudExpiredResults: 0, hudDeferredScans: 0, hudWaits: 0, hudRetryRecovered: 0, hudNativeExpired: 0, hudPanelGone: 0, hudRetryDropped: 0, feedRendered: 0, feedExpired: 0, feedDropped: 0, gameLogDrops: 0, lobbyRows: 0, skippedSelf: 0, skippedUnknown: 0, sendAttempts: 0, sendErrors: 0,
            channelWaits: 0, channelRetargets: 0, channelFaults: 0, hotkeyDrops: 0, classChannelReads: 0, localEchoReused: 0 } };
    // Bounded, typed event metadata only. Never put text, sender names, URLs or exception strings here.
    function audit(code, job, channel, reason, countValue, layout, ancestry, elapsedMs, feed) {
        if (state.logs.length >= 64) { state.logs.shift(); state.metrics.gameLogDrops++; }
        var event = { seq: ++state.logSeq, code: code };
        if (job) event.job = job;
        if (channel === 'team' || channel === 'all' || channel === 'party') event.channel = channel;
        if (reason) event.reason = reason;
        if (typeof countValue === 'number') event.count = countValue;
        if (layout) event.layout = layout;
        if (ancestry) event.ancestry = ancestry;
        if (feed) event.feed = feed;
        if (typeof elapsedMs === 'number' && isFinite(elapsedMs) && elapsedMs >= 0) event.elapsedMs = elapsedMs;
        state.logs.push(event);
    }
    audit('adapter-start');
    if (restartReason === 'root-moved') audit('adapter-root-moved');
    var control = { owner: context, afterSettingsClose: function () {
        if (stopped || !valid(context)) return;
        if (closeGuard !== null && $.CancelScheduled) { try { $.CancelScheduled(closeGuard); } catch (_) {} }
        closeGuard = $.Schedule(3.5, function () { closeGuard = null; if (!stopped) control.resume(false); });
    }, resume: function (force) {
        if (stopped || !valid(context) || !valid(ROOT) || ROOT.__dltOwner !== context) return false;
        if (rootOf(context) !== ROOT) { dispose(); initialize('root-moved'); return true; }
        var now = Date.now(), stalled = now - lastTickAt > 3000;
        var transportStale = !valid(state.bridge) || !inside(state.bridge, context) || !state.online || now - state.lastReplyAt > 15000;
        if (!stalled && (!force || !transportStale)) return true;
        if (!stalled && now - lastResumeAt < 5000) return true;
        lastResumeAt = now;
        resetTransport(); state.forcePoll = true; state.nextMetrics = 0;
        audit(stalled ? 'adapter-loop-resumed' : 'bridge-reconnect');
        if (stalled) {
            loopGeneration++;
            if (scheduled !== null && $.CancelScheduled) { try { $.CancelScheduled(scheduled); } catch (_) {} }
            scheduled = null; lastTickAt = now; armTick(0.1);
        }
        return true;
    } };
    ROOT.__dltAdapterControl = control;
    function valid(panel) { try { return !!panel && (!panel.IsValid || panel.IsValid()); } catch (_) { return false; } }
    function rootOf(panel) {
        for (var depth = 0; depth < 32 && valid(panel); depth++) {
            var parent = null; try { parent = panel.GetParent ? panel.GetParent() : null; } catch (_) { return null; }
            if (!parent) return panel; panel = parent;
        }
        return null;
    }
    function find(panel, id) { try { return valid(panel) ? panel.FindChildTraverse(id) : null; } catch (_) { return null; } }
    function classes(panel, name) { try { return panel.FindChildrenWithClassTraverse(name) || []; } catch (_) { return []; } }
    function text(panel) { try { return valid(panel) ? String(panel.text || '') : ''; } catch (_) { return ''; } }
    function count(panel) { try { return valid(panel) ? panel.GetChildCount() : 0; } catch (_) { return 0; } }
    function child(panel, i) { try { return panel.GetChild(i); } catch (_) { return null; } }
    function has(panel, name) { try { return valid(panel) && panel.BHasClass(name); } catch (_) { return false; } }
    function inside(panel, ancestor) {
        for (var depth = 0; depth < 32 && valid(panel); depth++) {
            if (panel === ancestor) return true;
            try { panel = panel.GetParent ? panel.GetParent() : null; } catch (_) { return false; }
        }
        return false;
    }
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
        if (stopped) return;
        if (!valid(context) || !valid(ROOT) || ROOT.__dltOwner !== context) { dispose(); return; }
        for (var p = 0; p < arguments.length; p++) {
            var panel = arguments[p];
            if (panel && typeof panel === 'object' && typeof panel.IsValid === 'function' && panel !== state.bridge) return;
        }
        for (var i = 0; i < arguments.length; i++) {
            var candidate = arguments[i];
            if (typeof candidate === 'string' && candidate.indexOf('DLT:') === 0) { state.eventTitle = candidate; return; }
        }
    }
    var titleEvents = ['HTMLChangedTitle', 'HTMLTitle'];
    if ($.RegisterForUnhandledEvent && $.UnregisterForUnhandledEvent) for (var e = 0; e < titleEvents.length; e++) {
        try { globalHandlers.push({ name: titleEvents[e], handle: $.RegisterForUnhandledEvent(titleEvents[e], titleEvent) }); } catch (_) {}
    }
    function unbindBridge() {
        if (valid(boundBridge) && $.UnregisterEventHandler) for (var e = 0; e < titleEvents.length; e++) {
            try { $.UnregisterEventHandler(titleEvents[e], boundBridge, titleEvent); } catch (_) {}
        }
        boundBridge = null;
    }
    function dispose() {
        if (stopped) return;
        stopped = true;
        loopGeneration++;
        if (scheduled !== null && $.CancelScheduled) { try { $.CancelScheduled(scheduled); } catch (_) {} }
        if (closeGuard !== null && $.CancelScheduled) { try { $.CancelScheduled(closeGuard); } catch (_) {} }
        closeGuard = null;
        scheduled = null;
        for (var i = 0; i < globalHandlers.length; i++) { try { $.UnregisterForUnhandledEvent(globalHandlers[i].name, globalHandlers[i].handle); } catch (_) {} }
        globalHandlers = []; unbindBridge();
        try { if (valid(context) && $.UnregisterEventHandler) $.UnregisterEventHandler('PanelDeleted', context, panelDeleted); } catch (_) {}
        try { if (valid(ROOT) && ROOT.__dltOwner === context) { ROOT.__dltOwner = null; ROOT.__dltTranslationProgress = null; } } catch (_) {}
        try { if (valid(ROOT) && ROOT.__dltAdapterControl === control) ROOT.__dltAdapterControl = null; } catch (_) {}
        // The engine owns native chat/HTML destruction. Never navigate or delete it during teardown.
        closeDisplayPreview(); clearFeed(true); clearNativeHudBindings(); state.hudRetries = []; state.records = []; state.recordsById = {}; state.render = []; state.outbox = []; state.hud = []; state.hudRoots = []; state.extraHudRoots = []; state.hudSearch = null;
        state.chats = []; state.outgoingQueue = []; state.outgoingKnown = {}; state.pending = null; state.pendingHotkey = null;
        state.chat = state.rootChat = state.rootMessages = state.lobby = state.input = state.targetLabel = state.bridge = state.menuMount = null;
        state.chatTarget.panel = null; state.closeWatch = null; state.echoes = []; state.outgoingStatus = []; state.recentOwn = []; state.heroRoster = []; state.heroTeams = [];
    }
    function panelDeleted(panel) { if (panel === context || !valid(context)) dispose(); }
    try { $.RegisterEventHandler('PanelDeleted', context, panelDeleted); } catch (_) {}
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
        state.chatTarget.source = ''; state.chatTarget.evidencePanel = null; state.chatTarget.conflict = false;
        var list = targetPanels();
        var found = '', foundPanel = null;
        for (var c = 0; c < list.length; c++) {
            var team = has(list[c], 'ChatTarget_GameAllies'), all = has(list[c], 'ChatTarget_GameAll'), party = has(list[c], 'ChatTarget_Party');
            var matches = (team ? 1 : 0) + (all ? 1 : 0) + (party ? 1 : 0);
            if (matches > 1) { state.chatTarget.conflict = true; return ''; }
            if (matches === 1) {
                var channel = team ? 'team' : all ? 'all' : 'party';
                if (found && found !== channel) { state.chatTarget.conflict = true; return ''; }
                found = channel; foundPanel = list[c];
            }
        }
        if (found) { state.metrics.classChannelReads++; state.chatTarget.source = 'classes'; state.chatTarget.evidencePanel = foundPanel; return found; }
        for (var i = 0; i < list.length; i++) {
            try {
                var raw = list[i].chat_target;
                if (raw === undefined || raw === null) continue;
                var name = targetFromNative(raw);
                if (name) { state.chatTarget.panel = list[i]; state.chatTarget.readable = true; state.chatTarget.source = 'property'; return name; }
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
    // Native submit retains its controller target when input closes, even if its
    // presentation classes temporarily disappear. Scope this evidence to the exact
    // controller/input and invalidate it synchronously on every native focus event.
    DLTChatInputFocused = function () {
        if (stopped) return;
        state.chatTarget.epoch++; state.nativeClosedAt = 0; state.nativeSettleUntil = Date.now() + 250;
    };
    function capturedChannelProof(channel) {
        var current = nativeChannel();
        return current === channel && state.chatTarget.epoch > 0 && state.chatTarget.source === 'classes' && !state.chatTarget.conflict ? { channel: channel, panel: state.chatTarget.evidencePanel, input: state.input, epoch: state.chatTarget.epoch } : null;
    }
    function proofCurrent(job, desired) {
        var proof = job.channelProof;
        return !!proof && proof.channel === desired && proof.epoch === state.chatTarget.epoch && proof.input === state.input && valid(proof.input) && valid(proof.panel) && targetPanels().indexOf(proof.panel) >= 0;
    }
    function channelEvidence(desired, job) {
        var now = channelNow(), code = function (value) { return value === 'team' ? 1 : value === 'all' ? 2 : value === 'party' ? 3 : 0; };
        return code(now.native) | code(now.label) << 2 | code(desired) << 4 | (state.chatTarget.source === 'classes' ? 1 : state.chatTarget.source === 'property' ? 2 : 0) << 6 | (state.chatTarget.conflict ? 256 : 0) | (proofCurrent(job, desired) ? 512 : 0);
    }
    function channelConfirmed(desired) {
        var now = channelNow();
        // A label is useful for capturing user intent, never for authorizing a send.
        return (desired === 'team' || desired === 'all' || desired === 'party') && now.native === desired;
    }
    function ensureChannel(job, desired) {
        if (channelConfirmed(desired)) { job.retryAt = 0; return 'ok'; }
        if (state.chatTarget.lastNative && state.chatTarget.lastNative !== desired || state.chatTarget.conflict) job.channelProof = null;
        if (!state.chatTarget.lastNative && !state.chatTarget.conflict && proofCurrent(job, desired)) {
            if (!job.proofLogged) { job.proofLogged = true; audit('channel-cached', job.id, desired, null, channelEvidence(desired, job)); }
            return 'ok';
        }
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
    function nativeInputOpen() {
        try {
            return valid(state.input) && (has(context, 'ChatExpanded') || has(state.rootChat, 'ChatExpanded') || !!(state.input.BHasKeyFocus && state.input.BHasKeyFocus()));
        } catch (_) { return false; }
    }
    function settingsVisible() { var window = ROOT.__dltActiveSettingsWindow; return state.openSettings || valid(window) && !window.__dltClosing; }
    function inputOpen() { return nativeInputOpen(); }
    function hasPendingOutgoing() {
        return Object.keys(state.outgoingKnown).some(function (id) { return !state.outgoingKnown[id].done; });
    }
    // Optional metadata only. Bodies are held solely by the existing task and the
    // short-lived local echo observers, never included in status/log records.
    function outgoingStatus(job, part, status, reason) {
        if (!state.statusSupported) return;
        if (status === 'waiting_input' || status === 'waiting_channel' || status === 'ready') {
            if (job.lastWait === status + ':' + (reason || '') + ':' + part) return;
            job.lastWait = status + ':' + (reason || '') + ':' + part;
            state.outgoingStatus = state.outgoingStatus.filter(function (x) { return x.id !== job.id || x.part !== part || ['waiting_input','waiting_channel','ready'].indexOf(x.state) < 0; });
        }
        state.outgoingStatus.push({ seq: ++state.statusSerial, id: job.id, part: part, state: status, channel: job.channel, at: Date.now(), reason: reason || '' });
        while (state.outgoingStatus.length > 96) state.outgoingStatus.shift();
        state.forcePoll = true;
    }
    function queueCaptured(job) {
        if (!job || !job.capture) return;
        if (job.captureRegistered) { job.result = null; outgoingStatus(job, 0, 'capture_ready'); }
        else state.outgoingQueue.push(job.capture);
        job.capture = null; state.forcePoll = true;
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
        if (reason !== 'captured') audit('hotkey-dropped', null, null, reason === 'input-not-open' ? 'no-chat' : reason === 'unsupported-channel' ? 'unknown-channel' : reason.indexOf('backend-outdated') === 0 ? 'old-client' : reason);
        if (reason !== 'captured') logChannel('hotkey ignored: ' + reason);
    }
    function captureOutgoing() {
        var held = state.pendingHotkey;
        if (settingsVisible()) { hotkeyOutcome('input-not-open'); return; }
        if (!state.config.outgoingEnabled) { hotkeyOutcome('disabled'); return; }
        if (!inputOpen()) {
            // The reported chat focus can lag the keypress by a bridge round trip. Keep the chord
            // for a short bounded window and retry instead of losing it silently.
            if (held && Date.now() - held.at > 2500) { state.metrics.hotkeyDrops++; hotkeyOutcome('input-not-open'); }
            return;
        }
        var input = state.input, value = text(input).trim();
        if (!value) { hotkeyOutcome('empty'); return; }
        if (outgoingCharacterCount(value) > 80) { hotkeyOutcome('input-truncated'); return; }
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
        if ((state.config.outgoingTargetLanguage || 'en') !== 'en' && !state.multiLanguageAvailable) {
            state.metrics.hotkeyDrops++; hotkeyOutcome('backend-outdated-language');
            logChannel('language target requires a matching backend; draft preserved'); return;
        }
        var id = 'out-' + String(++state.serial);
        var captured = { id: id, text: value, channel: channel, displayMode: outgoingDisplayMode(), separateBilingual: !!state.config.outgoingSeparateBilingual, splitLong: !!state.config.outgoingSplitLong,
            targetLanguage: state.config.outgoingTargetLanguage || 'en', customLanguage: state.config.outgoingCustomLanguage || '',
            customLanguageCode: state.config.outgoingCustomLanguageCode || '' };
        state.outgoingKnown[id] = { id: id, channel: channel, capture: captured, timeoutSeconds: state.config.outgoingTimeoutSeconds, ordinal: ++state.outgoingOrdinal, channelProof: capturedChannelProof(channel), created: Date.now(), attempted: false, done: false, waiting: false, retargets: 0,
            retryAt: 0, before: '', restored: false, original: value, displayMode: outgoingDisplayMode() };
        // New backends register the retained source immediately, but may not start
        // translation until the close gate releases it. Legacy backends keep the
        // snapshot locally until that gate, without receiving an unknown feature.
        if (state.statusSupported) {
            captured.holdForInput = true; state.outgoingKnown[id].captureRegistered = true;
            state.outgoingQueue.push(captured); state.forcePoll = true;
        }
        noteChannel('capture', { native: nativeName, label: labelName, channel: channel });
        audit('hotkey-captured', id, channel);
        input.text = '';
        requestNativeClose(id, channel);
        hotkeyOutcome('captured');
    }
    function requestNativeClose(id, channel) {
        var now = Date.now();
        state.nativeClosedAt = 0; state.nativeSettleUntil = now + 250;
        state.closeWatch = { id: id, channel: channel, epoch: state.chatTarget.epoch, started: now, lastAttempt: now, attempts: 1, closedAt: 0, confirmed: false, confirmedTurn: 0, input: state.input };
        audit('send-close-request', id, channel);
        // Never send a user draft as a side effect of a close request.
        if (!valid(state.input) || text(state.input)) return;
        $.DispatchEvent(stockSubmit(), state.input);
        $.DispatchEvent('CitadelChatInputBlur', state.input);
    }
    function watchClose() {
        var watch = state.closeWatch;
        if (!watch) return;
        var now = Date.now(), elapsed = now - watch.started, input = state.input;
        if (watch.epoch !== state.chatTarget.epoch || input !== watch.input || text(input) !== '' || settingsVisible()) {
            // The player is editing again; never submit or close their new input.
            state.closeWatch = null;
            audit('send-close-interrupted', watch.id, watch.channel);
            noteChannel('close', { result: 'player-typing' });
            return;
        }
        var focused = false; try { focused = !!input.BHasKeyFocus && input.BHasKeyFocus(); } catch (_) {}
        if (!nativeInputOpen() && !focused) {
            if (!watch.closedAt) watch.closedAt = now;
            if (!watch.confirmed && now - watch.closedAt >= 250) {
                watch.confirmed = true; watch.confirmedTurn = state.turn; state.nativeSettleUntil = now + 100; restoreAfterClose(watch.channel, watch.id);
                queueCaptured(state.outgoingKnown[watch.id]);
                audit('send-close-confirmed', watch.id, watch.channel, null, watch.attempts, null, null, elapsed);
            }
            // Retain the epoch-scoped watch briefly to catch a native close rebound.
            if (elapsed >= 1500) state.closeWatch = null;
            return;
        }
        watch.closedAt = 0; watch.confirmed = false; state.nativeClosedAt = 0;
        if (elapsed >= 1500 || !valid(input)) {
            state.closeWatch = null; audit('send-close-timeout', watch.id, watch.channel, 'native-close', watch.attempts);
            var blocked = watch.id && state.outgoingKnown[watch.id];
            if (blocked && !blocked.done && !blocked.attempted) { queueCaptured(blocked); refuseOutgoing(watch.id, blocked, 'native-close'); }
            noteChannel('close', { result: 'still-open' }); return;
        }
        if (watch.attempts >= 3 || now - watch.lastAttempt < 250) return;
        watch.attempts++;
        watch.lastAttempt = now; state.nativeSettleUntil = now + 250;
        $.DispatchEvent(stockSubmit(), input);
        $.DispatchEvent('CitadelChatInputBlur', input);
        audit('send-close-retry', watch.id, watch.channel, null, watch.attempts);
        noteChannel('close', { result: 'retry-' + watch.attempts });
        logChannel('chat box stayed open; retried the native close (' + watch.attempts + ')');
    }
    // Panorama's TextEntry stores Unicode scalar values, not JavaScript code units.
    // CitadelChat applies SetMaxChars(80) after the XML layout has loaded.
    function outgoingCharacterCount(value) {
        var total = 0;
        for (var i = 0; i < value.length; i++, total++) {
            var code = value.charCodeAt(i);
            if (code >= 0xd800 && code <= 0xdbff) {
                var next = value.charCodeAt(i + 1);
                if (!(next >= 0xdc00 && next <= 0xdfff)) return -1;
                i++;
            } else if (code >= 0xdc00 && code <= 0xdfff) return -1;
        }
        return total;
    }
    function sendPayload(result) {
        var mode = result.displayMode === 'bilingual' ? 'bilingual' : 'translation';
        var value = mode === 'bilingual' ? result.sendText : (result.sendText || result.english);
        return { mode: mode, value: String(value === undefined || value === null ? '' : value) };
    }
    function cacheOutgoing(results) {
        for (var i = 0; i < results.length; i++) {
            var result = results[i], job = state.outgoingKnown[result.id];
            if (!job && ['ready','waiting_channel','waiting_input'].indexOf(result.state) < 0) continue;
            if (!job && result.isPreset === true) {
                var knownKeys = Object.keys(state.outgoingKnown);
                for (var x = 0; knownKeys.length > 48 && x < knownKeys.length; x++) if (state.outgoingKnown[knownKeys[x]].done) { delete state.outgoingKnown[knownKeys[x]]; knownKeys.pop(); }
                job = state.outgoingKnown[result.id] = { id: result.id, channel: result.channel, ordinal: ++state.outgoingOrdinal, created: Date.now(), original: result.original, displayMode: 'translation', done: false, nextPart: 0, retargets: 0, retryAt: 0, before: '', restored: false };
            }
            if (job && !job.done) job.result = result;
        }
        // Keep replay protection for pending acknowledgements, not an unbounded
        // history of every chat in a long lobby session.
        var keys = Object.keys(state.outgoingKnown), remaining = keys.length;
        for (var old = 0; remaining > 96 && old < keys.length; old++) {
            var id = keys[old], previous = state.outgoingKnown[id];
            if (previous.done && state.outgoingSent.indexOf(id) < 0 && !state.outgoingPartAcks.some(function (ack) { return ack.id === id; })) {
                delete state.outgoingKnown[id]; remaining--;
            }
        }
    }
    function processOutgoing() {
        if (!hasPendingOutgoing()) return;
        var now = Date.now(), input = state.input, focused = false;
        try { focused = valid(input) && input.BHasKeyFocus && input.BHasKeyFocus(); } catch (_) {}
        var idle = valid(input) && !focused && !nativeInputOpen() && !settingsVisible();
        if (!idle) state.nativeClosedAt = 0;
        else if (!state.nativeClosedAt) state.nativeClosedAt = now;
        var keys = Object.keys(state.outgoingKnown).sort(function (a,b) { return state.outgoingKnown[a].ordinal - state.outgoingKnown[b].ordinal; });
        for (var i = 0; i < keys.length; i++) {
            var job = state.outgoingKnown[keys[i]], result = job.result;
            if (job.done) continue;
            if (now - job.created > ((job.timeoutSeconds || state.config.outgoingTimeoutSeconds) + 5) * 1000) { queueCaptured(job); refuseOutgoing(job.id, job, 'timeout'); job.result = null; audit('send-failed', job.id, job.channel, 'timeout'); continue; }
            // A user reopen cancels automatic closing, not the captured task.
            if (job.capture && idle && now >= state.nativeSettleUntil && now - state.nativeClosedAt >= 250 && (!state.closeWatch || state.closeWatch.confirmed && state.closeWatch.confirmedTurn < state.turn)) { queueCaptured(job); continue; }
            if (job.capture) { outgoingStatus(job, 0, 'waiting_input', settingsVisible() ? 'settings' : 'input'); continue; }
            if (!result) continue;
            if (result.state === 'waiting_input' && !result.sendText) continue; // retained registration, not a translated payload
            var part = Number(result.partIndex || 0), parts = Number(result.partCount || 1);
            if (parts < 1 || parts > 8 || part < 0 || part >= parts || part % 1 || parts % 1) continue;
            if (job && part !== (job.nextPart || 0)) continue;
            if (!job || job.done) continue;
            if (result.state === 'failed') { job.done = true; audit('send-failed', result.id, result.channel); continue; }
            if (!state.config.outgoingEnabled) { refuseOutgoing(result.id, job, 'disabled'); continue; }
            if ((['ready','waiting_channel','waiting_input'].indexOf(result.state) < 0) || job.attempted || Date.now() < state.nextSendAt) continue;
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
            var payloadCharacters = outgoingCharacterCount(payload.value);
            if (payloadCharacters < 1 || payloadCharacters > 80 || /[\x00-\x1f\x7f";\\]/.test(payload.value)) {
                state.metrics.sendErrors++;
                refuseOutgoing(result.id, job, 'invalid-send-text');
                continue;
            }
            // The native input is also the user's editor. Defer while they are typing;
            // never close a newly opened chat or change the current chat channel.
            if (!idle || now < state.nativeSettleUntil || now - state.nativeClosedAt < 250 || state.closeWatch && (!state.closeWatch.confirmed || state.closeWatch.confirmedTurn === state.turn)) {
                outgoingStatus(job, part, 'waiting_input', settingsVisible() ? 'settings' : 'input'); continue;
            }
            if (keys.some(function (id) { var earlier = state.outgoingKnown[id]; return !earlier.done && earlier.channel === result.channel && earlier.ordinal < job.ordinal; })) continue;
            if (state.channelFault) {
                outgoingStatus(job, part, 'waiting_channel', 'channel-fault');
                if (!job.waiting) { job.waiting = true; state.metrics.channelWaits++; audit('channel-wait', result.id, result.channel, 'unknown-channel', channelEvidence(result.channel, job)); }
                logChannel('held: an earlier message appeared in a different channel (' + state.channelFault + ')');
                continue;
            }
            var verdict = ensureChannel(job, result.channel);
            if (verdict === 'pending') continue;
            if (verdict !== 'ok') {
                outgoingStatus(job, part, 'waiting_channel', 'channel');
                if (!job.waiting) { job.waiting = true; state.metrics.channelWaits++; audit('channel-wait', result.id, result.channel, 'unknown-channel', channelEvidence(result.channel, job)); }
                job.channelWaitReason = verdict;
                continue;
            }
            job.waiting = false; outgoingStatus(job, part, 'ready');
            job.attempted = true; // Never repeat a command after an uncertain dispatch.
            var draft = text(state.input);
            try {
                state.input.text = payload.value;
                if (text(state.input) !== payload.value) {
                    // A future game build or another UI mod may impose a smaller limit.
                    // Read the actual stored value before dispatch; never send a prefix.
                    state.metrics.sendErrors++;
                    refuseOutgoing(result.id, job, 'input-truncated');
                    continue;
                }
                state.nextSendAt = Date.now() + 900;
                var accepted = $.DispatchEvent(stockSubmit(), state.input);
                state.metrics.sendAttempts++;
                // Native "keep text" and party chat can retain the entry value
                // after an accepted event. Input clearing is not a receipt: never
                // cancel the translated remainder based on this presentation state.
                if (accepted !== false) {
                    if (parts > 1) state.outgoingPartAcks.push({ id: result.id, part: part }); else state.outgoingSent.push(result.id);
                    job.nextPart = part + 1;
                    audit('send-submitted', result.id, result.channel, null, part);
                    outgoingStatus(job, part, 'dispatched');
                    state.echoes.push({ id: result.id, part: part, text: normalized(payload.value), channel: result.channel, at: Date.now(), checked: false });
                    while (state.echoes.length > 32) finishEcho(state.echoes.shift(), 'echo_unobserved', 'capacity');
                    state.recentOwn.push({ text: payload.value, original: job.original || result.original || '', channel: result.channel,
                        displayMode: job.displayMode || result.displayMode, at: Date.now(), views: {} });
                    while (state.recentOwn.length > 32) state.recentOwn.shift();
                    noteChannel('submit', { channel: result.channel, result: 'dispatched' });
                } else { state.metrics.sendErrors++; noteChannel('submit', { channel: result.channel, result: 'rejected' }); refuseOutgoing(result.id, job, 'submit-rejected'); }
                job.done = job.refused || part + 1 >= parts;
                if (!job.done) job.attempted = false;
            } catch (_) { job.done = true; state.metrics.errors++; state.metrics.sendErrors++; refuseOutgoing(result.id, job, 'submit-error'); }
            finally {
                if (valid(state.input)) state.input.text = draft;
                restoreChannel(job);
                if (!job.done) { job.retargets = 0; job.retryAt = 0; job.before = ''; job.restored = false; }
            }
        }
    }
    function finishEcho(echo, status, reason) {
        if (!echo || echo.checked) return;
        echo.checked = true;
        var job = state.outgoingKnown[echo.id];
        if (job) outgoingStatus(job, echo.part, status, reason);
        audit(status === 'echo_observed' ? 'send-echo-observed' : 'send-echo-unobserved', echo.id, echo.channel, null, echo.part);
    }
    function expireEchoes() {
        var now = Date.now();
        for (var i = 0; i < state.echoes.length; i++) if (now - state.echoes[i].at >= 8000) finishEcho(state.echoes[i], 'echo_unobserved', 'timeout');
        state.echoes = state.echoes.filter(function (x) { return now - x.at < 8000; });
    }
    // A local row is observation only. Repeated identical bodies cannot identify
    // a unique task/part, including duplicate chat/HUD views of the same row.
    function checkEcho(value, channel, role, freshText) {
        if (!freshText || role !== 'self' || ['team','all','party'].indexOf(channel) < 0) return;
        var matches = state.echoes.filter(function (x) { return Date.now() - x.at < 8000 && x.text === value; });
        if (matches.length !== 1) {
            for (var i = 0; i < matches.length; i++) finishEcho(matches[i], 'echo_unobserved', 'ambiguous');
            return;
        }
        var echo = matches[0]; if (echo.checked) return;
        if (channel === echo.channel) { finishEcho(echo, 'echo_observed'); noteChannel('echo', { channel: channel, result: 'observed-local' }); return; }
        finishEcho(echo, 'echo_unobserved', 'channel-mismatch');
        state.channelFault = echo.channel + '->' + channel; state.metrics.channelFaults++;
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
        var panels = [discoveryFind('TeamFriendly'), discoveryFind('TeamEnemy')];
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
    function rowRole(row, kind, team, channelText) {
        if (has(row, 'IsSelf') || classes(row, 'SenderLocalClient').length) return 'self';
        if (has(row, 'IsEnemy') || has(row, 'enemy')) return 'enemy';
        var channel = channelText;
        if (channelOf(channel) === 'team' || channelOf(channel) === 'party') return 'peer';
        if (has(row, 'friend') || has(row, 'IsFriendly') || has(row, 'IsAlly')) return 'peer';
        if (kind === 'hud' && state.team && team) return team === state.team ? 'peer' : 'enemy';
        return 'unknown';
    }
    function rowText(row, kind, contents) {
        if (kind === 'hud') return normalized(text(hudSource(row).text));
        if (kind === 'lobby') return contentText(classes(row, 'ChatLine')[0]);
        return contentText(contents);
    }
    function shouldTranslate(value) {
        if (!value || value.length > 1024) return false;
        if ((state.config.incomingTargetLanguage || 'zh-Hans') !== 'zh-Hans' && /[\u3400-\u9fff\uf900-\ufaff]/.test(value)) return true;
        // Chinese + numbers/punctuation has no translation work. Kana and other scripts still translate.
        return /[A-Za-z\u0370-\u052f\u0530-\u058f\u0590-\u06ff\u0900-\u0dff\u10a0-\u10ff\u3040-\u30ff\uac00-\ud7af\u0600-\u06ff\u00c0-\u024f\u0e00-\u0e7f]/.test(value);
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
        if (!valid(row) || (kind === 'hud' && has(row, 'Expired'))) return;
        state.metrics.rows++; state.metrics[kind + 'Rows']++;
        var source = kind === 'hud' ? hudSource(row) : null;
        var contents = source ? source.host : find(row, 'MessageContents');
        var quick = has(row, 'Ping') || has(contents, 'Ping') || !!find(row, 'PingLabel') || (kind === 'hud' && (has(row, 'MessageType_MapPing') || has(row, 'MessageType_AbilityPing') || has(row, 'MessageType_PingWheel') || has(row, 'MessageType_ItemDraftReaction') || has(row, 'MessageType_PlayerTyping')));
        // Read these controls once per scan, rather than repeating tree searches for text, role and channel.
        var channelText = text(classes(row, kind === 'lobby' ? 'ChatLinePrefix' : 'ChannelName')[0]);
        var value = source ? normalized(text(source.text)) : rowText(row, kind, contents), role = rowRole(row, kind, team, channelText);
        // Missing children during native snippet rebuilding are not a new empty message.
        // Preserve the old binding until the original returns or a real new text is seen.
        if (source && !quick && (!valid(source.text) || !value)) return;
        var channel = kind === 'hud' ? 'hud' : channelOf(channelText) || (kind === 'lobby' ? 'lobby' : '');
        var sender = text(classes(row, kind === 'lobby' ? 'ChatPersona' : 'SenderName')[0]).slice(0, 100);
        var nativeId = nativeMessageId(row);
        var identity = readHeroIdentity(row, contents, value + '\n' + sender, kind, team, sender, nativeId);
        var freshText = value !== row.__dltEchoObservedText;
        row.__dltEchoObservedText = value;
        checkEcho(value, channel, role, freshText);
        var scope = state.config.incomingScope === 'teammates' ? 'teammates' : 'all_players';
        var receiveTarget = state.config.incomingTargetLanguage || 'zh-Hans';
        var targetKey = receiveTarget === 'custom' ? [receiveTarget, state.config.incomingCustomLanguage || '', state.config.incomingCustomLanguageCode || ''].join('|') : receiveTarget;
        var fingerprint = targetKey + '\n' + value + '\n' + sender + '\n' + role + '\n' + channel + '\n' + String(quick) + '\n' + scope + '\n' + String(!!state.config.enabled) + '\n' + String(!!state.config.outgoingUseContext) + '\n' + nativeId;
        if (row.__dltFingerprint === fingerprint && row.__dltSession === state.session) {
            var cached = row.__dltRecord, ownedLabel = row.__dltLabel;
            if (cached && !showRecord(cached)) return;
            if (cached && updateIdentity(cached, identity, sender)) queueRender(cached);
            if (kind === 'hud' && independentDisplay() && cached && cached.translation && !cached.feedEnded && !state.feedSlots.some(function (slot) { return slot.record === cached && slot.revision === cached.revision; })) queueRender(cached);
            if (kind === 'hud' && !nativeDisplay()) {
                if (cached && cached.translation && !cached.feedEnded && !state.feedSlots.some(function (slot) { return slot.record === cached && slot.revision === cached.revision; })) queueRender(cached);
                return;
            }
            // Native snippets can rebuild their children while retaining the ChatMessage panel.
            // Recover only a still-live view of the same record, without a duplicate request.
            var wrongHost = kind === 'hud' && valid(ownedLabel) && (!inside(ownedLabel, contents || row) || ownedLabel.GetParent() !== contents);
            var hiddenLabel = kind === 'hud' && ownHudLabelHidden(ownedLabel);
            if (cached && state.recordsById[cached.id] === cached && cached.translation && !(kind === 'hud' && nativeHudUpdateBlocked(ownedLabel, cached)) &&
                (!valid(ownedLabel) || !inside(ownedLabel, row) || wrongHost || hiddenLabel || !(kind === 'hud' ? nativeHudCurrent(ownedLabel, cached) : text(ownedLabel) === nativeLabelText(cached)) || !labelFontCurrent(ownedLabel))) {
                queueRender(cached);
                if (kind === 'hud') state.metrics.hudRecovered++;
            }
            return;
        }
        if (valid(row.__dltLabel)) { if (kind === 'hud') clearNativeHudLabel(row.__dltLabel); else row.__dltLabel.text = ''; }
        row.__dltFingerprint = fingerprint; row.__dltSession = state.session; row.__dltRecord = null;
        if (role === 'self' && !roleAllowed(role)) state.metrics.skippedSelf++;
        if (role === 'unknown' && !roleAllowed(role)) state.metrics.skippedUnknown++;
        var translateWanted = state.config.enabled && !quick && shouldTranslate(value) && roleAllowed(role);
        var contextWanted = state.config.outgoingUseContext && !quick && value && value.length <= 1024 && (channel === 'team' || channel === 'all' || channel === 'party');
        if (!translateWanted && !contextWanted) return;
        var own = null, now = Date.now();
        while (state.recentOwn.length && now - state.recentOwn[0].at > 8000) state.recentOwn.shift();
        if (role === 'self' && targetKey === 'zh-Hans') for (var e = state.recentOwn.length - 1; e >= 0; e--) {
            var sent = state.recentOwn[e];
            if (!sent.views[kind] && sent.text === value && (channel === sent.channel || kind === 'hud')) { own = sent; sent.views[kind] = true; break; }
        }
        if (translateWanted && own && own.original && /[\u3400-\u9fff]/.test(own.original)) {
            state.metrics.localEchoReused++;
            if (contextWanted && state.outbox.length < 64) state.outbox.push({ id: 'ctx-' + String(++state.serial), text: value, sender: '', channel: channel, contextOnly: true });
            if (own.displayMode === 'bilingual') return; // The original Chinese is already in the sent line.
            var local = { id: 'local-' + String(++state.serial), original: value, sender: sender, role: role, channel: channel,
                created: now, revision: 1, done: true, translation: own.original, stage: 'local', isSelf: true,
                targetKey: targetKey, wantsTranslation: true,
                hudSeen: kind === 'hud' && row.__dltRetiredFeedFingerprint !== fingerprint, displayChannel: own.channel,
                views: [{row:row,kind:kind,fingerprint:fingerprint}] };
            local.nativeId = nativeId; updateIdentity(local, identity, sender);
            state.records.push(local); state.recordsById[local.id] = local; row.__dltRecord = local; queueRender(local); return;
        }
        var record = null;
        for (var i = state.records.length - 1; i >= 0; i--) {
            var candidate = state.records[i];
            // Both native views carry chat_message_id. Different IDs must never merge,
            // even when two players send the same text in the same scan.
            if (nativeId && candidate.nativeId && nativeId !== candidate.nativeId) continue;
            var sameNative = !!nativeId && nativeId === candidate.nativeId;
            if (!sameNative && Date.now() - candidate.created > 2500) continue;
            if (candidate.original !== value || candidate.targetKey !== targetKey) continue;
            if (identity && candidate.hero && candidate.hero !== identity.name) continue;
            if (translateWanted && !candidate.wantsTranslation) continue;
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
            // A saturated bridge is temporary. Do not permanently mark a visible row as
            // handled before its task can enter the outbox; the next bounded scan retries it.
            if (state.outbox.length >= 64) { row.__dltFingerprint = null; if (kind === 'hud') state.metrics.hudDeferredScans++; return; }
            record = { id: String(++state.serial), original: value, sender: sender, role: role, channel: channel,
                created: Date.now(), nativeId: nativeId, targetKey: targetKey, wantsTranslation: translateWanted, revision: 0, done: !translateWanted, translation: '', stage: '', views: [] };
            state.records.push(record); state.recordsById[record.id] = record;
            state.outbox.push({ id: record.id, text: value, sender: sender, channel: channel, contextOnly: !translateWanted });
        }
        if (!record.nativeId && nativeId) record.nativeId = nativeId;
        if (role === 'self') record.isSelf = true;
        updateIdentity(record, identity, sender);
        record.views.push({ row: row, kind: kind, fingerprint: fingerprint });
        if (kind === 'hud') {
            record.hudSeen = row.__dltRetiredFeedFingerprint !== fingerprint;
            if (has(row, 'MessageTarget_GameAll')) record.displayChannel = 'all';
            else if (has(row, 'MessageTarget_GameAllies')) record.displayChannel = 'team';
            state.metrics.hudBindings++; audit('hud-bound', record.id);
        }
        row.__dltRecord = record;
        if (record.translation) queueRender(record);
    }
    function scanContainer(container, kind, team, limit) {
        var n = count(container), tail = Math.min(n,limit), older = n - limit;
        var tailState = container && container.__dltTailState;
        var tailChanged = !tailState || tailState.count !== n;
        if (tailChanged) tailState = { count: n, cursor: 0 };
        var canOlder = older > 0 && (kind === 'hud' || inputOpen());
        var olderFirst = canOlder && !!container.__dltOlderTurn;
        if (canOlder) container.__dltOlderTurn = !olderFirst;
        // Split the row allowance and alternate which range goes first. Otherwise
        // a permanent 24-row tail consumes all 24 slots and old rows never run;
        // a slow first native lookup must not starve one range forever either.
        var quota = canOlder ? Math.max(1,Math.floor((state.readBudget ? state.readBudget.left : limit*2)/2)) : tail;
        if (tailChanged) tailState.cursor = Math.max(0,tail-Math.min(tail,canOlder ? quota : tail));
        for (var phase = 0; phase < (canOlder ? 2 : 1); phase++) {
            var oldRange = canOlder && (phase === 0 ? olderFirst : !olderFirst);
            var size = oldRange ? older : tail;
            var cursor = oldRange ? container.__dltScanCursor || 0 : tailState.cursor;
            if (cursor >= size) cursor = 0;
            var wanted = Math.min(size,canOlder ? quota : tail), read = 0;
            for (; read < wanted; read++) {
                if (state.readBudget && (state.readBudget.left <= 0 || Date.now() >= state.readBudget.until)) { performanceBucket().deferredWork++; break; }
                if (state.readBudget) state.readBudget.left--;
                var index = oldRange ? (cursor+read)%size : n-tail+(cursor+read)%size;
                try { observe(child(container,index),kind,team); } catch (_) { state.metrics.errors++; }
            }
            if (oldRange) container.__dltScanCursor = size ? (cursor+read)%size : 0;
            else tailState.cursor = size ? (cursor+read)%size : 0;
        }
        if (container) container.__dltTailState = tailState;
    }
    function scanChat() {
        // All history owners share one row/deadline budget. Include the legacy
        // lobby in the rotation so a busy regular tail cannot consume its turn.
        var regular = state.chats.length, total = regular + (valid(state.lobby) ? 1 : 0);
        if (!total) return;
        var start = (state.chatScanCursor || 0) % total;
        for (var i = 0; i < total; i++) {
            var index = (start+i) % total;
            scanContainer(index < regular ? state.chats[index] : state.lobby, index < regular ? 'regular' : 'lobby', 0, 24);
        }
        state.chatScanCursor = (start+1) % total;
    }
    function discoveryFind(id) {
        var panel = state.discoveryIndex[id];
        return inside(panel, ROOT) ? panel : null;
    }
    function stepDiscovery() {
        // Native roots are frequently appended at a scene transition. Probe only
        // the first/last few direct children before continuing the bounded walk.
        function remember(panel) {
            if (!valid(panel)) return;
            var id = String(panel.id || '');
            if (['Chat', 'ChatMessages', 'ChatLinesPanel', 'Team1Chat', 'Team2Chat', 'SubOptions', 'TeamFriendly', 'TeamEnemy'].indexOf(id) >= 0) state.discoveryIndex[id] = panel;
        }
        var rootCount = count(ROOT);
        for (var edge = 0; edge < Math.min(16, rootCount); edge++) {
            remember(child(ROOT, edge));
            if (rootCount - 1 - edge >= 16) remember(child(ROOT, rootCount - 1 - edge));
        }
        var walk = state.discoveryWalk;
        if (!walk) walk = state.discoveryWalk = { stack: [{ panel: ROOT, index: -1 }], visited: 0 };
        var started = Date.now(), work = 0;
        while (walk.stack.length && work < 64 && Date.now() - started < 2) {
            var frame = walk.stack[walk.stack.length - 1], panel = frame.panel;
            work++;
            if (!valid(panel)) { walk.stack.pop(); continue; }
            if (frame.index === -1) {
                walk.visited++; frame.index = 0;
                var id = String(panel.id || '');
                remember(panel);
                if (id === 'ChatMessages' || id === 'ChatLinesPanel' || id === 'Messages' || walk.stack.length >= 64) { walk.stack.pop(); continue; }
            }
            if (frame.index >= count(panel)) { walk.stack.pop(); continue; }
            walk.stack.push({ panel: child(panel, frame.index++), index: -1 });
        }
        if (!walk.stack.length) state.discoveryWalk = null;
        return !!state.discoveryWalk;
    }
    function discover() {
        var walking = stepDiscovery();
        if (!valid(state.chat)) state.chat = find(context, 'ChatMessages');
        if (!valid(state.rootChat)) state.rootChat = discoveryFind('Chat');
        if (!valid(state.rootMessages)) state.rootMessages = find(state.rootChat, 'ChatMessages') || discoveryFind('ChatMessages');
        state.chats = [];
        var candidates = [state.chat, state.rootMessages];
        for (var c = 0; c < candidates.length; c++) if (valid(candidates[c]) && state.chats.indexOf(candidates[c]) < 0) state.chats.push(candidates[c]);
        if (!valid(state.lobby)) state.lobby = discoveryFind('ChatLinesPanel');
        ensureBridge();
        if (!valid(state.input)) state.input = find(context, 'ChatInput');
        if (!valid(state.targetLabel)) state.targetLabel = find(context, 'ChatTargetLabel');
        // Searching the full root is limited to infrequent discovery, never the normal scan loop.
        var team = localTeam();
        state.team = team; state.hudRoots = state.hudRoots || [];
        var hudIds = ['Team1Chat', 'Team2Chat'];
        for (var h = 0; h < hudIds.length; h++) {
            if (!inside(state.hudRoots[h], ROOT)) state.hudRoots[h] = discoveryFind(hudIds[h]);
        }
        refreshBubbles();
        try {
            if (!inside(state.menuMount, ROOT)) {
                state.menuMount = null;
                var settings = discoveryFind('SubOptions');
                // The settings entry must not wait minutes behind incremental HUD
                // discovery. Use the engine's established targeted lookup only while
                // missing, with backoff; never repeat it while our mount is attached.
                if (!valid(settings) && Date.now() >= state.nextMenuProbe) {
                    settings = find(ROOT, 'SubOptions');
                    state.nextMenuProbe = Date.now() + (state.menuProbeMisses < 5 ? 2000 : 10000);
                    if (valid(settings)) { state.discoveryIndex.SubOptions = settings; state.menuProbeMisses = 0; }
                    else state.menuProbeMisses++;
                }
                if (valid(settings)) {
                    state.menuMount = find(settings, 'DltMenuMount');
                    if (!valid(state.menuMount)) {
                        var settingsButton = find(settings, 'settings');
                        var nativeRow = valid(settingsButton) && settingsButton.GetParent ? settingsButton.GetParent() : null;
                        var inline = valid(nativeRow) && has(nativeRow, 'SettingsRow');
                        var mount = $.CreatePanel('Panel', inline ? nativeRow : settings, 'DltMenuMount');
                        mount.style.width = 'fit-children';
                        if (inline) mount.AddClass('DltMenuInlineMount');
                        var menuLoaded = mount.BLoadLayout('file://{resources}/layout/dlt_menu.xml', false, false);
                        if (menuLoaded === false) { mount.DeleteAsync(0); audit('menu-failed'); throw new Error('DLT menu layout failed'); }
                        audit('menu-mounted');
                        // A menu variant without the native settings row gets one compact fallback row.
                        // Even in that case, never move QOL or native primary buttons.
                        var quit = !inline ? find(settings, 'quit') : null;
                        if (quit && settings.MoveChildBefore) settings.MoveChildBefore(mount, quit);
                        state.menuMount = mount;
                    }
                }
            }
        } catch (_) { state.metrics.errors++; }
        state.nextDiscovery = Date.now() + (walking ? 50 : state.hud.length ? 10000 : 2000);
    }
    function scanBubbles() {
        // Message containers are rebuilt independently of the chat and their HUD root.
        // Resolve only inside two cached roots each scan; full-root discovery remains infrequent.
        discoverExtraHudRoots(); refreshBubbles(); retryHud(); maintainFeed();
        var start = state.hudScanCursor || 0;
        for (var h = 0; h < state.hud.length; h++) { var current = state.hud[(start+h)%state.hud.length]; scanContainer(current.panel, 'hud', current.team, 12); }
        state.hudScanCursor = state.hud.length ? (start+1)%state.hud.length : 0;
    }
    function explicitlyHidden(panel) {
        for (var d = 0; d < 32 && valid(panel); d++) {
            try { if (panel.visible === false) return true; panel = panel.GetParent ? panel.GetParent() : null; } catch (_) { return true; }
        }
        return false;
    }
    function discoverExtraHudRoots() {
        // An old HUD can remain valid and attached while its replacement has the same IDs.
        // Walk at most 64 discovery edges per scan, skip chat/message subtrees, and cache
        // at most eight roots. Never perform an unbounded full-root traversal in this loop.
        function remember(item) {
            var id = ''; try { id = item.id || ''; } catch (_) { return false; }
            if (id !== 'Team1Chat' && id !== 'Team2Chat') return false;
            var seed = state.hudRoots || [], extras = state.extraHudRoots;
            if (seed.indexOf(item) < 0 && !extras.some(function (entry) { return entry.root === item; })) {
                extras.push({ root: item, team: id === 'Team1Chat' ? 2 : 3 });
                if (extras.length > 6) extras.shift();
            }
            return true;
        }
        // Native team HUDs share a small ChatContainer. Catch a replacement there
        // immediately even if neither its old root nor the outer screen was destroyed.
        var parents = [], seeds = state.hudRoots || [];
        for (var s = 0; s < seeds.length; s++) if (valid(seeds[s])) {
            var parent = null; try { parent = seeds[s].GetParent(); } catch (_) {}
            if (valid(parent) && parents.indexOf(parent) < 0) parents.push(parent);
        }
        for (var a = 0; a < parents.length; a++) for (var c = 0, size = Math.min(count(parents[a]), 16); c < size; c++) remember(child(parents[a], c));
        var n = count(ROOT), search = state.hudSearch, now = Date.now();
        if (!search || search.rootCount !== n || (!search.stack.length && now >= search.next)) {
            search = state.hudSearch = { rootCount: n, stack: [{ panel: ROOT, index: n - 1, depth: 0 }], next: now + 2000 };
        }
        for (var budget = 0; budget < 64 && search.stack.length; budget++) {
            var frame = search.stack[search.stack.length - 1];
            if (!valid(frame.panel) || frame.index < 0) { search.stack.pop(); continue; }
            var item = child(frame.panel, frame.index--); if (!valid(item)) continue;
            var id = ''; try { id = item.id || ''; } catch (_) { continue; }
            if (remember(item)) continue;
            if (item === context || id === 'DltIncomingFeed' || id === 'DltDisplayPreview' || id === 'ChatMessages' || id === 'ChatLinesPanel' || id === 'Messages') continue;
            var children = count(item);
            if (children && frame.depth < 24) search.stack.push({ panel: item, index: children - 1, depth: frame.depth + 1 });
        }
        if (!search.stack.length && budget > 0) search.next = now + 2000;
    }
    function refreshBubbles() {
        var next = [], roots = state.hudRoots || [], lost = false, candidates = [];
        for (var h = 0; h < 2; h++) {
            if (!inside(roots[h], ROOT)) { if (roots[h]) lost = true; roots[h] = null; continue; }
            candidates.push({ root: roots[h], team: h + 2 });
        }
        state.extraHudRoots = state.extraHudRoots.filter(function (entry) { return inside(entry.root, ROOT); });
        candidates = candidates.concat(state.extraHudRoots);
        for (var h = 0; h < candidates.length; h++) {
            var root = candidates[h].root, team = candidates[h].team;
            if (explicitlyHidden(root)) continue;
            // Messages is a direct child in both verified native layouts. Never traverse its
            // expanding message history just to locate the container every 200 milliseconds.
            var messages = null;
            for (var c = 0, n = Math.min(count(root), 16); c < n; c++) {
                var candidate = child(root, c);
                try { if (valid(candidate) && candidate.id === 'Messages') { messages = candidate; break; } } catch (_) {}
            }
            if (!valid(messages)) continue;
            var previous = root.__dltBoundSession === state.session ? root.__dltBoundMessages : null;
            if (previous !== messages) { state.metrics.hudRebinds++; audit('hud-rebound', null, null, null, team); }
            root.__dltBoundSession = state.session; root.__dltBoundMessages = messages;
            if (!next.some(function (entry) { return entry.panel === messages; })) next.push({ root: root, panel: messages, team: team });
        }
        state.hudRoots = roots; state.hud = next; state.metrics.hudContainers = next.length;
        if (lost) state.nextDiscovery = Math.min(state.nextDiscovery, Date.now());
    }
    function queueRender(record) { if (!record.renderQueued) { record.renderQueued = true; state.render.push(record); } }
    function hasLiveBubble(record) {
        return record.views.some(function (view) { return view.kind === 'hud' && valid(view.row) && !has(view.row, 'Expired') && view.row.__dltRecord === record && state.hud.some(function (entry) { return inside(view.row, entry.panel); }); });
    }
    function nextRender(preferBubble) {
        if (preferBubble) for (var i = 0; i < state.render.length; i++) if (hasLiveBubble(state.render[i])) return state.render.splice(i, 1)[0];
        return state.render.shift();
    }
    // Use the live source's ancestry, rather than the first duplicate MessageContents ID.
    // Retired snippets may remain attached with exactly the same IDs.
    function displayHidden(panel) {
        for (var depth = 0; depth < 32 && valid(panel); depth++) {
            try {
                var style = panel.style || {};
                if (panel.visible === false || style.visibility === 'collapse' || style.visibility === 'hidden' || zeroOpacity(style.opacity)) return true;
                panel = panel.GetParent ? panel.GetParent() : null;
            } catch (_) { return true; }
        }
        return false;
    }
    function hudSource(row) {
        var first = find(row, 'MessageText'), selected = first;
        if (!valid(first) || displayHidden(first)) {
            var stack = [row]; selected = null;
            for (var budget = 0; budget < 48 && stack.length; budget++) {
                var item = stack.pop(); if (!valid(item)) continue;
                if (item.id === 'MessageText' && !displayHidden(item)) { selected = item; break; }
                if (has(item, 'DltBubbleTranslation')) continue;
                for (var i = Math.min(count(item), 16) - 1; i >= 0; i--) stack.push(child(item, i));
            }
            if (!selected) selected = first;
        }
        var host = selected;
        for (var depth = 0; depth < 16 && valid(host) && host !== row; depth++) {
            if (host.id === 'MessageContents') return { text: selected, host: host };
            try { host = host.GetParent(); } catch (_) { break; }
        }
        return { text: selected, host: row };
    }
    function nativeDisplayState(view, record) {
        var row = view.row;
        if (!valid(row)) return 'panel-gone';
        if (has(row, 'Expired')) return 'native-expired';
        if (row.__dltRecord !== record || row.__dltFingerprint !== view.fingerprint || row.__dltSession !== state.session) return 'message-changed';
        if (!inside(row, ROOT)) return 'detached';
        if (displayHidden(row)) return 'hidden';
        if (!state.hud.some(function (entry) { return inside(row, entry.panel); })) return 'container-missing';
        var source = hudSource(row);
        if (!valid(source.text) || !valid(source.host)) return 'host-missing';
        if (displayHidden(source.text)) return 'hidden';
        if (normalized(text(source.text)) !== record.original) return 'message-changed';
        return '';
    }
    function waitForHud(record, view, reason) {
        if (view.displayState !== reason) { view.displayState = reason; state.metrics.hudWaits++; audit('hud-wait', record.id, null, reason); }
        if (record.hudRetryQueued) return;
        if (state.hudRetries.length >= 64) {
            var dropped = state.hudRetries.shift(); dropped.hudRetryQueued = false;
            state.metrics.hudRetryDropped++; audit('hud-retry-ended', dropped.id, null, 'queue-full');
        }
        record.hudRetryQueued = true;
        record.hudRetryUntil = record.hudRetryUntil || Date.now() + 30000;
        record.hudRetryAt = Date.now() + 400;
        state.hudRetries.push(record);
    }
    function retryHud() {
        // Fixed work budget; reuse the existing scan loop instead of a per-message timer.
        for (var i = 0, n = Math.min(4, state.hudRetries.length); i < n; i++) {
            var record = state.hudRetries.shift(); record.hudRetryQueued = false;
            if (state.recordsById[record.id] !== record) continue;
            if (!record.hudRetryUntil) continue;
            if (Date.now() >= record.hudRetryUntil) {
                state.metrics.hudRetryDropped++; audit('hud-retry-ended', record.id, null, 'retry-expired'); continue;
            }
            if (Date.now() < record.hudRetryAt) { record.hudRetryQueued = true; state.hudRetries.push(record); continue; }
            queueRender(record);
        }
    }
    function independentDisplay() { return state.config.incomingDisplayMode === 'independent' || state.config.incomingDisplayMode === 'both'; }
    function nativeDisplay() { return state.config.incomingDisplayMode !== 'independent'; }
    function appearance(options) {
        var c = state.config, o = options || {};
        function number(key, fallback, min, max) { var n = Number(o[key]); return isFinite(n) ? Math.max(min, Math.min(max, n)) : fallback; }
        return { mode: o.mode || c.incomingDisplayMode || 'native', opacity: number('opacity', Number(c.incomingOpacity) || 90, 20, 100),
            width: number('width', Number(c.incomingFeedWidth) || 340, 240, 600), position: o.position || c.incomingFeedPosition || 'center-left',
            offsetX: number('offsetX', typeof c.incomingFeedOffsetX === 'number' ? c.incomingFeedOffsetX : 28, 0, 120), offsetY: number('offsetY', Number(c.incomingFeedOffsetY) || 0, 0, 120),
            fontSize: number('fontSize', Number(c.fontSize) || 17, 12, 24), seconds: number('seconds', Number(c.incomingFeedSeconds) || 8, 3, 15), lines: number('lines', Number(c.incomingFeedLines) || 3, 1, 10),
            showHeroName: typeof o.showHeroName === 'boolean' ? o.showHeroName : c.incomingShowHeroName !== false,
            showAccountName: typeof o.showAccountName === 'boolean' ? o.showAccountName : c.incomingShowAccountName !== false,
            heroNamePosition: o.heroNamePosition || c.incomingHeroNamePosition || 'before', chineseNameStyle: o.chineseNameStyle || c.chineseNameStyle || 'current' };
    }
    function heroLabel(raw) {
        if (typeof raw !== 'string' && typeof raw !== 'number') return null;
        var value = String(raw === undefined || raw === null ? '' : raw).toLowerCase().trim().replace(/^#/, '').replace(/:n$/, '');
        if (!value || value.length > 240) return null;
        var entry = heroLabels[value]; if (entry) return entry;
        var basename = value.replace(/\\/g, '/').split('/').pop().replace(/\.(vtex_c|vtex|png|psd|svg)$/, '').replace(/_(png|psd|svg)$/, '');
        return heroLabels[basename] || null;
    }
    function nativeMessageId(row) {
        // The current native history and top-bar builders both set this UInt32 attribute.
        // It is local display metadata; never send it to a translation provider or log it.
        var value = null;
        try { if (row.GetAttributeUInt32) value = row.GetAttributeUInt32('chat_message_id', 4294967295); } catch (_) {}
        if (value === null || value === undefined || value === 4294967295) {
            try { if (row.GetAttributeInt) value = row.GetAttributeInt('chat_message_id', -1); } catch (_) {}
        }
        if (typeof value !== 'number' || value < 0 || value >= 4294967295 || value !== Math.floor(value)) return '';
        return String(value);
    }
    function refreshHeroRoster() {
        var now = Date.now();
        if (now < state.nextHeroRoster) return state.heroRoster;
        state.nextHeroRoster = now + 1000;
        var next = [], ids = ['TeamFriendly', 'TeamEnemy'];
        if (!inside(state.heroTeams[0], ROOT) || !inside(state.heroTeams[1], ROOT)) {
            // Team panels are siblings of the cached native chat roots, inside
            // TeamsContainer. Do not traverse the full game root when they are absent.
            var parents = [ROOT], roots = state.hudRoots || [];
            for (var h = 0; h < roots.length && parents.length < 9; h++) {
                var parent = roots[h];
                for (var depth = 0; depth < 4 && valid(parent); depth++) {
                    try { parent = parent.GetParent(); } catch (_) { break; }
                    if (valid(parent) && parents.indexOf(parent) < 0) parents.push(parent);
                }
            }
            for (var p = 0; p < parents.length; p++) for (var c = 0, total = Math.min(32, count(parents[p])); c < total; c++) {
                var item = child(parents[p], c), id = '';
                try { id = item.id; } catch (_) {}
                if (id === 'TeamFriendly') state.heroTeams[0] = item;
                else if (id === 'TeamEnemy') state.heroTeams[1] = item;
                else if (id === 'TeamsContainer') for (var t = 0, teams = Math.min(12, count(item)); t < teams; t++) {
                    var teamPanel = child(item, t), teamId = '';
                    try { teamId = teamPanel.id; } catch (_) {}
                    if (teamId === 'TeamFriendly') state.heroTeams[0] = teamPanel;
                    else if (teamId === 'TeamEnemy') state.heroTeams[1] = teamPanel;
                }
            }
        }
        for (var t = 0; t < ids.length; t++) {
            var panel = state.heroTeams[t];
            if (!inside(panel, ROOT)) continue;
            var team = has(panel, 'team1') ? 2 : has(panel, 'team2') ? 3 : 0;
            if (!team) continue;
            var players = find(panel, 'PlayersContainer');
            for (var i = 0, n = Math.min(12, count(players)); i < n; i++) {
                var player = child(players, i);
                // These are native labels, also retained by QOL. Unlike CitadelHeroImage,
                // Label.text is readable in Panorama. Read hidden labels without showing them.
                var hero = heroLabel(text(classes(player, 'HeroName')[0])) || heroLabel(text(classes(player, 'HeroNameHidden')[0]));
                if (!hero) {
                    var badge = find(player, 'HeroBadge');
                    try { if (valid(badge)) hero = heroLabel(badge.heroid); } catch (_) {}
                }
                var account = normalized(text(classes(player, 'PlayerName')[0]) || text(classes(player, 'AlwaysPlayerName')[0])).slice(0, 100);
                if (!hero && !account) continue;
                next.push({ name: hero ? hero.name : '', legacy: hero ? hero.legacy || hero.name : '', account: account,
                    team: team, index: i + 1, local: has(player, 'LocalPlayer') });
            }
        }
        // Never retain the previous roster after labels/panels disappear during a transition.
        state.heroRoster = next;
        var resolved = next.filter(function (entry) { return !!entry.name; }).length;
        if (state.heroRosterCount !== resolved) { state.heroRosterCount = resolved; audit('hero-roster', null, null, null, resolved); }
        return next;
    }
    function rosterHero(row, kind, team, sender) {
        if (kind === 'lobby') return null;
        var roster = refreshHeroRoster(), matches = [], index = 0;
        if (kind === 'hud') {
            // Native PlayerIndexN is 1 + the speaker's child index in its own team's
            // PlayersContainer. It is not a global player slot or a hero ID.
            for (var i = 1; i <= 12; i++) if (has(row, 'PlayerIndex' + i)) {
                if (index) return null;
                index = i;
            }
            if (index) {
                for (var i = 0; i < roster.length; i++) if (roster[i].team === team && roster[i].index === index) matches.push(roster[i]);
                return matches.length === 1 && matches[0].name ? matches[0] : null;
            }
        }
        var self = has(row, 'IsSelf') || classes(row, 'SenderLocalClient').length > 0;
        // No nickname-to-hero guessing. A history nickname must exactly match a single
        // actual roster entry and the row must have the native in-match sender marker.
        if (!self && (kind !== 'regular' || !sender || !classes(row, 'SenderHeroImage').length)) return null;
        for (var i = 0; i < roster.length; i++) if (self ? roster[i].local : roster[i].account === normalized(sender)) matches.push(roster[i]);
        return matches.length === 1 && matches[0].name ? matches[0] : null;
    }
    function readHeroIdentity(row, contents, original, kind, team, sender, nativeId) {
        var key = state.session + '\n' + nativeId + '\n' + original;
        if (row.__dltIdentityText === key && (row.__dltHeroIdentity || Date.now() < (row.__dltIdentityNext || 0))) return row.__dltHeroIdentity || null;
        row.__dltIdentityText = key; row.__dltIdentityNext = Date.now() + 1000;
        var result = rosterHero(row, kind, team, sender);
        // HeroImage belongs to the speaker; TargetHeroImage belongs to a ping target.
        // Older/custom layouts may expose a readable property; current CitadelHeroImage
        // only exports SetHeroID. It must not be the sole identity source.
        var image = find(contents, 'HeroImage') || find(row, 'HeroImage');
        if (!result && valid(image)) {
            var keys = ['heroname', 'heroid', 'heroID', 'hero_name', 'heroName'];
            for (var i = 0; i < keys.length && !result; i++) { try { result = heroLabel(image[keys[i]]); } catch (_) {} }
            if (!result && image.GetAttributeString) for (var i = 0; i < keys.length && !result; i++) { try { result = heroLabel(image.GetAttributeString(keys[i], '')); } catch (_) {} }
            if (!result && image.GetAttributeInt) { try { result = heroLabel(image.GetAttributeInt('heroid', -1)); } catch (_) {} }
            if (!result && image.GetImage) { try { result = heroLabel(image.GetImage()); } catch (_) {} }
        }
        row.__dltHeroIdentity = result; return result;
    }
    function updateIdentity(record, identity, sender) {
        var old = (record.hero || '') + '|' + (record.displaySender || '');
        if (identity && !record.hero) {
            record.hero = identity.name; record.legacyHero = identity.legacy || identity.name;
            audit('hero-resolved', record.id);
        }
        if (sender || (identity && identity.account)) record.displaySender = sender || identity.account;
        if (!record.hero && record.wantsTranslation !== false && state.config.incomingShowHeroName !== false && !record.heroMissingReported) { record.heroMissingReported = true; audit('hero-unavailable', record.id); }
        return old !== (record.hero || '') + '|' + (record.displaySender || '');
    }
    function identityText(record, options, unknown) {
        var o = options || appearance(), hero = o.showHeroName ? (o.chineseNameStyle === 'legacy' ? record.legacyHero || record.hero : record.hero) : '';
        if (!hero && o.showHeroName && unknown) hero = '未知角色';
        var account = o.showAccountName ? record.displaySender || record.sender || '' : '';
        if (hero && account) return o.heroNamePosition === 'after' ? account + '（' + hero + '）' : '（' + hero + '）' + account;
        return hero ? '（' + hero + '）' : account;
    }
    function nativeLabelText(record) {
        // Native chat already provides sender identity. Additional names and hero
        // preferences belong only to the independent feed's information row.
        return record.translation;
    }
    function showRecord(record) {
        return state.config.incomingShowOwnTranslations !== false || !(record.isSelf || record.role === 'self');
    }
    function hideOwnRecord(record, quiet) {
        for (var i = 0; i < record.views.length; i++) {
            var view = record.views[i], row = view.row;
            if (!valid(row) || row.__dltRecord !== record) continue;
            if (view.kind === 'hud') clearNativeHudLabel(row.__dltLabel);
            else if (valid(row.__dltLabel) && has(row.__dltLabel, 'DltTranslation')) {
                row.__dltLabel.text = ''; row.__dltLabel.visible = false;
            }
        }
        // Keep an already active card's deadline. Never replay hidden backlog
        // when the user later re-enables the independent display.
        if (!state.feedSlots.some(function (slot) { return slot.record === record && slot.until > Date.now(); })) record.feedEnded = true;
        if (!record.ownHiddenReported) { record.ownHiddenReported = true; if (!quiet) audit('own-hidden', record.id); }
    }
    function syncOwnDisplay() {
        var enabled = state.config.incomingShowOwnTranslations !== false;
        if (enabled === state.showOwnDisplay) return;
        state.showOwnDisplay = enabled; audit('own-display', null, null, null, enabled ? 1 : 0);
        for (var i = 0; i < state.records.length; i++) {
            var record = state.records[i]; if (!(record.isSelf || record.role === 'self')) continue;
            if (enabled) { record.ownHiddenReported = false; queueRender(record); }
            else hideOwnRecord(record, true);
        }
    }
    function feedCaption(record, options) {
        var channel = record.displayChannel || record.channel;
        var channelName = channel === 'team' ? '友军' : channel === 'all' ? '全体' : channel === 'party' ? '小队' : '聊天';
        var identity = identityText(record, options, true);
        return (identity ? identity.slice(0, 100) + ' · ' : '') + channelName + (record.stage === 'local' ? ' · 原文回显' : record.stage === 'quality' ? ' · 精翻' : ' · 快译');
    }
    function layoutFeed(panel, options) {
        var o = options || appearance(), signature = JSON.stringify(o);
        if (panel.__dltLayout === signature) return;
        panel.__dltLayout = signature;
        var horizontal = /left$/.test(o.position) ? 'left' : /right$/.test(o.position) ? 'right' : 'center';
        var vertical = /^top/.test(o.position) ? 'top' : /^bottom/.test(o.position) ? 'bottom' : 'center';
        // Panorama percentage limits can resolve against a transient settings/layout
        // parent. Honor the requested pixel width through every owned layer instead.
        var style = { width: o.width + 'px', minWidth: o.width + 'px', maxWidth: o.width + 'px', overflow: 'noclip', opacity: String(o.opacity / 100), horizontalAlign: horizontal, verticalAlign: vertical, marginLeft: '0px', marginRight: '0px', marginTop: '0px', marginBottom: '0px' };
        style[horizontal === 'right' ? 'marginRight' : 'marginLeft'] = o.offsetX + 'px';
        style[vertical === 'bottom' ? 'marginBottom' : 'marginTop'] = o.offsetY + 'px'; feedStyle(panel, style);
    }
    function layoutFeedDensity(slots, options) {
        var compact = options.lines > 5, height = compact ? Math.floor(540 / options.lines) - 6 : 84;
        var signature = options.lines + '|' + options.width, cardWidth = options.width + 'px';
        var textWidth = Math.max(120, options.width - (compact ? 16 : 20) - 3) + 'px';
        for (var i = 0; i < slots.length; i++) {
            var slot = slots[i]; if (slot.density === signature) continue; slot.density = signature;
            feedStyle(slot.panel, { flowChildren: 'down', width: cardWidth, minWidth: cardWidth, maxWidth: cardWidth, borderRadius: '4px', height: 'fit-children', minHeight: compact ? '32px' : '42px', maxHeight: height + 'px', padding: compact ? '4px 8px' : '7px 10px' });
            feedStyle(slot.title, { width: textWidth, minWidth: textWidth, maxWidth: textWidth, marginRight: '0px', whiteSpace: 'nowrap', fontSize: compact ? '11px' : '13px', maxHeight: compact ? '14px' : '20px' });
            feedStyle(slot.text, { width: textWidth, minWidth: textWidth, maxWidth: textWidth, whiteSpace: 'normal', maxHeight: compact ? Math.max(18, height - 22) + 'px' : '50px' });
        }
    }
    function feedStyle(panel, values) { for (var key in values) { try { panel.style[key] = values[key]; } catch (_) {} } }
    function revealFeedPanel(panel) {
        if (!valid(panel) || !ownHudLabelHidden(panel)) { if (valid(panel)) panel.__dltFeedRestore = null; return false; }
        var signature = [panel.visible, panel.style.visibility, panel.style.opacity].join('|'), attempt = panel.__dltFeedRestore;
        if (attempt && attempt.key === signature && attempt.count >= 2) return false;
        panel.__dltFeedRestore = { key: signature, count: attempt && attempt.key === signature ? attempt.count + 1 : 1 };
        try {
            if (panel.visible === false) panel.visible = true;
            if (panel.style.visibility === 'collapse' || panel.style.visibility === 'hidden') panel.style.visibility = 'visible';
            if (zeroOpacity(panel.style.opacity)) panel.style.opacity = '1';
        } catch (_) { return false; }
        return !ownHudLabelHidden(panel);
    }
    function feedEvidence(slot) {
        function number(panel, key) { try { var n = panel[key]; return typeof n === 'number' && isFinite(n) ? n : null; } catch (_) { return null; } }
        var flags = 0, panels = [state.feed, slot.panel, slot.title, slot.text];
        for (var i = 0; i < panels.length; i++) {
            if (ownHudLabelHidden(panels[i])) flags |= 1 << i;
            try {
                if (typeof panels[i].BReadyForDisplay === 'function' && !panels[i].BReadyForDisplay()) flags |= 64;
                if (typeof panels[i].BIsTransparent === 'function' && panels[i].BIsTransparent()) flags |= 128;
            } catch (_) {}
        }
        if (!inside(state.feed, ROOT)) flags |= 16;
        try { if (slot.panel.GetParent() !== state.feed || slot.title.GetParent() !== slot.panel || slot.text.GetParent() !== slot.panel) flags |= 32; } catch (_) { flags |= 32; }
        if (number(slot.text, 'actuallayoutwidth') === 0 || number(slot.text, 'actuallayoutheight') === 0) flags |= 256;
        return { rootWidth: number(state.feed, 'actuallayoutwidth'), rootHeight: number(state.feed, 'actuallayoutheight'),
            rootX: number(state.feed, 'actualxoffset'), rootY: number(state.feed, 'actualyoffset'),
            cardWidth: number(slot.panel, 'actuallayoutwidth'), cardHeight: number(slot.panel, 'actuallayoutheight'),
            textWidth: number(slot.text, 'actuallayoutwidth'), textHeight: number(slot.text, 'actuallayoutheight'),
            flags: flags, remainingMs: Math.max(0, slot.until - Date.now()) };
    }
    function sampleFeed() {
        for (var i = 0, n = Math.min(2, state.feedChecks.length); i < n; i++) {
            var check = state.feedChecks.shift(), slot = check.slot;
            if (check.record !== slot.record || !valid(slot.panel) || !valid(slot.text) || !valid(state.feed) || Date.now() >= slot.until || !showRecord(slot.record)) continue;
            if (Date.now() < check.due) { state.feedChecks.push(check); continue; }
            audit('feed-layout', slot.record.id, null, null, check.sample, null, null, null, feedEvidence(slot));
            if (check.sample === 1) { check.sample = 2; check.due = Date.now() + 750; state.feedChecks.push(check); }
        }
    }
    function clearFeed(remove) {
        for (var i = 0; i < state.feedSlots.length; i++) {
            var slot = state.feedSlots[i];
            if (slot.record) slot.record.feedEnded = true;
            if (valid(slot.panel)) slot.panel.visible = false;
            if (valid(slot.title)) slot.title.text = '';
            if (valid(slot.text)) slot.text.text = '';
            slot.record = null;
        }
        if (remove) {
            if (valid(state.feed) && state.feed.__dltOwnedFeed && inside(state.feed, ROOT)) {
                try { state.feed.DeleteAsync(0); } catch (_) {}
            }
            state.feed = null; state.feedSlots = [];
        }
        state.feedChecks = [];
    }
    function ensureFeed() {
        if (valid(state.feed) && inside(state.feed, ROOT) && state.feedSlots.length === 10 && state.feedSlots.every(function (slot) { return valid(slot.panel) && valid(slot.title) && valid(slot.text) && slot.panel.GetParent() === state.feed && slot.title.GetParent() === slot.panel && slot.text.GetParent() === slot.panel; })) return true;
        if (Date.now() < state.nextFeedCreate) return false;
        var previous = state.feedSlots.slice(0, 10);
        for (var oldSlot = 0; oldSlot < previous.length; oldSlot++) if (valid(previous[oldSlot].panel) && previous[oldSlot].panel.__dltOwnedFeedSlot) previous[oldSlot].panel.visible = false;
        if (valid(state.feed) && state.feed.__dltOwnedFeed && inside(state.feed, ROOT)) { try { state.feed.visible = false; state.feed.DeleteAsync(0); } catch (_) {} }
        // Only own leaf controls; no native XML replacement, input handler or focus capture.
        try {
            state.feed = $.CreatePanel('Panel', ROOT, 'DltIncomingFeed'); state.feed.__dltOwnedFeed = true;
            state.feed.hittest = false; state.feed.hittestchildren = false;
            feedStyle(state.feed, { flowChildren: 'down', width: '350px', height: 'fit-children', maxHeight: '600px',
                visibility: 'visible', horizontalAlign: 'left', verticalAlign: 'center', marginLeft: '28px', marginTop: '0px' });
            state.feedSlots = [];
            for (var i = 0; i < 10; i++) {
                var panel = $.CreatePanel('Panel', state.feed, 'DltFeedSlot' + i);
                panel.__dltOwnedFeedSlot = true;
                panel.hittest = false; panel.hittestchildren = false; panel.visible = false;
                feedStyle(panel, { flowChildren: 'down', width: '100%', height: 'fit-children', maxHeight: '110px',
                    backgroundColor: '#191d1dec', borderLeft: '3px solid #b99860', padding: '7px 10px', marginBottom: '6px' });
                var title = $.CreatePanel('Label', panel, 'DltFeedTitle' + i), label = $.CreatePanel('Label', panel, 'DltFeedText' + i);
                title.html = label.html = false; title.hittest = label.hittest = false;
                feedStyle(title, { color: '#c7ae80', fontSize: '13px', width: '100%', maxHeight: '20px', textOverflow: 'ellipsis' });
                feedStyle(label, { color: '#f0e5d0', width: '100%', height: 'fit-children', maxHeight: '70px', whiteSpace: 'normal', textOverflow: 'shrink' });
                state.feedSlots.push({ panel: panel, title: title, text: label, record: null, until: 0, shownAt: 0, revision: 0 });
            }
            for (var i = 0; i < previous.length; i++) {
                var old = previous[i], replacement = state.feedSlots[i];
                if (!old.record || old.until <= Date.now()) continue;
                replacement.record = old.record; replacement.until = old.until; replacement.shownAt = old.shownAt; replacement.revision = old.revision;
                replacement.title.text = old.caption || ''; replacement.caption = old.caption || '';
                replacement.text.text = old.record.translation; replacement.text.style.fontSize = state.config.fontSize + 'px'; replacement.panel.visible = true;
            }
            state.nextFeedCreate = 0; audit('feed-mounted'); return true;
        } catch (_) { state.nextFeedCreate = Date.now() + 3000; audit('feed-unavailable'); return false; }
    }
    function maintainFeed() {
        maintainDisplayPreview();
        var mode = state.config.incomingDisplayMode || 'native';
        if (state.feedMode !== mode) {
            if (mode === 'native') clearFeed(false); state.feedMode = mode; state.hudRetries = [];
            // Existing original messages remain untouched; remove only our native translations.
            for (var i = 0; i < state.records.length; i++) for (var v = 0; v < state.records[i].views.length; v++) {
                state.records[i].hudRetryQueued = false; state.records[i].hudRetryUntil = 0;
                var view = state.records[i].views[v];
                if (mode === 'independent' && view.kind === 'hud' && valid(view.row) && valid(view.row.__dltLabel)) clearNativeHudLabel(view.row.__dltLabel);
            }
            audit('display-mode');
        }
        if (!state.config.enabled || mode === 'native') { if (valid(state.feed)) state.feed.visible = false; return; }
        if (state.feed && !ensureFeed()) return;
        if (!valid(state.feed)) return;
        if (revealFeedPanel(state.feed)) { state.feed.__dltLayout = null; audit('feed-restored', null, null, null, 1); }
        layoutFeed(state.feed); layoutFeedDensity(state.feedSlots, appearance());
        var now = Date.now(), maxLines = Math.max(1, Math.min(10, Number(state.config.incomingFeedLines) || 3));
        for (var i = 0; i < state.feedSlots.length; i++) {
            var slot = state.feedSlots[i];
            if (slot.record && (now >= slot.until || i >= maxLines)) {
                audit('feed-ended', slot.record.id); state.metrics.feedExpired++;
                slot.record.feedEnded = true; slot.record = null; slot.panel.visible = false; slot.title.text = ''; slot.text.text = '';
            }
            if (slot.record) {
                if (!showRecord(slot.record)) { slot.panel.visible = false; continue; }
                var restored = revealFeedPanel(slot.panel), titleRestored = revealFeedPanel(slot.title), textRestored = revealFeedPanel(slot.text);
                if (restored || titleRestored || textRestored) audit('feed-restored', slot.record.id, null, null, (restored ? 2 : 0) | (titleRestored ? 4 : 0) | (textRestored ? 8 : 0), null, null, null, feedEvidence(slot));
                updateLabel(slot.text, slot.record.translation);
                var caption = feedCaption(slot.record, appearance());
                if (slot.title.text !== caption) { slot.title.text = caption; slot.caption = caption; }
            }
        }
        sampleFeed();
    }
    function renderFeed(record) {
        // Native HUD binding proves this is a current received message, not old history
        // discovered by opening chat. Preserve eligibility after that bubble expires.
        if (!showRecord(record) || !record.hudSeen || record.feedEnded || !record.translation || Date.now() - record.created > 90000) return;
        if (!ensureFeed()) return;
        maintainFeed(); state.feed.visible = true;
        var slot = null, assigned = false, now = Date.now(), maxLines = Math.max(1, Math.min(10, Number(state.config.incomingFeedLines) || 3));
        for (var i = 0; i < maxLines; i++) if (state.feedSlots[i].record === record) slot = state.feedSlots[i];
        if (!slot) {
            for (var i = 0; i < maxLines; i++) if (!state.feedSlots[i].record) { slot = state.feedSlots[i]; break; }
            if (!slot) {
                slot = state.feedSlots[0];
                for (var i = 1; i < maxLines; i++) if (state.feedSlots[i].shownAt < slot.shownAt) slot = state.feedSlots[i];
                slot.record.feedEnded = true; state.metrics.feedDropped++; audit('feed-displaced', slot.record.id);
            }
            slot.record = record; slot.shownAt = now; assigned = true;
            slot.until = now + Math.max(3, Math.min(15, Number(state.config.incomingFeedSeconds) || 8)) * 1000;
        }
        var caption = feedCaption(record, appearance());
        if (!assigned && slot.revision === record.revision && slot.title.text === caption && slot.text.text === record.translation && slot.panel.visible !== false) return;
        slot.title.text = caption;
        slot.caption = slot.title.text;
        updateLabel(slot.text, record.translation); slot.panel.visible = true;
        slot.revision = record.revision; state.metrics.feedRendered++; audit('feed-written', record.id, null, null, null, null, null, null, feedEvidence(slot));
        state.feedChecks = state.feedChecks.filter(function (entry) { return entry.slot !== slot; });
        if (state.feedChecks.length >= 20) state.feedChecks.shift();
        state.feedChecks.push({ slot: slot, record: record, due: now + 250, sample: 1 });
    }
    function closeDisplayPreview() {
        if (valid(state.previewRoot) && state.previewRoot.__dltOwnedPreview && inside(state.previewRoot, ROOT)) { try { state.previewRoot.DeleteAsync(0); } catch (_) {} }
        state.previewRoot = null;
        if (state.preview) state.preview.active = false;
    }
    function acceptDisplayPreview(command) {
        if (!command || typeof command.epoch !== 'string' || command.epoch.length > 40 || typeof command.revision !== 'number') return;
        if (state.preview && state.preview.epoch === command.epoch && command.revision <= state.preview.revision) return;
        if (!command.active || !command.options) { closeDisplayPreview(); state.preview = { epoch: command.epoch, revision: command.revision, active: false }; return; }
        state.preview = { epoch: command.epoch, revision: command.revision, active: true,
            until: Date.now() + Math.max(0, Math.min(12000, Number(command.remainingMs) || 0)), options: appearance(command.options) };
        drawDisplayPreview();
    }
    function drawDisplayPreview() {
        var preview = state.preview; if (!preview || !preview.active) return;
        var root = state.previewRoot;
        if (!valid(root) || !inside(root, ROOT)) {
            root = state.previewRoot = $.CreatePanel('Panel', ROOT, 'DltDisplayPreview'); root.__dltOwnedPreview = true;
            root.hittest = false; root.hittestchildren = false; feedStyle(root, { width: '100%', height: '100%', zIndex: '300' });
            root.nativePanel = $.CreatePanel('Panel', root, 'DltPreviewNative'); root.nativePanel.hittest = false; root.nativePanel.hittestchildren = false;
            feedStyle(root.nativePanel, { flowChildren: 'down', horizontalAlign: 'center', verticalAlign: 'top', marginTop: '28px', width: '340px', padding: '10px', backgroundColor: '#e9dec8f2', borderLeft: '2px solid #b99860' });
            root.nativeTitle = $.CreatePanel('Label', root.nativePanel, 'DltPreviewNativeTitle'); root.nativeTitle.html = false; root.nativeTitle.hittest = false;
            root.nativeTitle.text = '原生气泡样式测试 · wait for my ult'; feedStyle(root.nativeTitle, { color: '#65533c', fontSize: '13px' });
            root.nativeText = $.CreatePanel('Label', root.nativePanel, 'DltPreviewNativeText'); root.nativeText.html = false; root.nativeText.hittest = false;
            feedStyle(root.nativeText, { color: '#28241e', width: '100%', whiteSpace: 'normal' });
            root.feedPanel = $.CreatePanel('Panel', root, 'DltPreviewFeed'); root.feedPanel.hittest = false; root.feedPanel.hittestchildren = false;
            feedStyle(root.feedPanel, { flowChildren: 'down', height: 'fit-children', maxHeight: '600px' }); root.samples = [];
            for (var i = 0; i < 10; i++) {
                var card = $.CreatePanel('Panel', root.feedPanel, 'DltPreviewCard' + i); card.hittest = false; card.hittestchildren = false;
                feedStyle(card, { flowChildren: 'down', width: '100%', backgroundColor: '#191d1dec', borderLeft: '3px solid #b99860', padding: '7px 10px', marginBottom: '6px' });
                var title = $.CreatePanel('Label', card, 'DltPreviewTitle' + i), label = $.CreatePanel('Label', card, 'DltPreviewText' + i);
                title.html = label.html = false; title.hittest = label.hittest = false;
                feedStyle(title, { color: '#c7ae80', fontSize: '13px', width: '100%', textOverflow: 'ellipsis' });
                feedStyle(label, { color: '#f0e5d0', width: '100%', maxHeight: '70px', whiteSpace: 'normal', textOverflow: 'shrink' });
                root.samples.push({ panel: card, title: title, text: label });
            }
        }
        var options = preview.options;
        var sample = { hero: '劳什', legacyHero: '神鞭', sender: '测试玩家', displaySender: '测试玩家', channel: 'team', stage: 'quality' };
        root.nativePanel.visible = options.mode !== 'independent'; root.feedPanel.visible = options.mode !== 'native';
        root.nativePanel.style.opacity = String(options.opacity / 100); root.nativeText.style.fontSize = options.fontSize + 'px';
        root.nativeText.text = '等我大招好了再一起推进。'; layoutFeed(root.feedPanel, options); layoutFeedDensity(root.samples, options);
        for (var i = 0; i < root.samples.length; i++) {
            var slot = root.samples[i]; slot.panel.visible = i < options.lines;
            slot.title.text = '显示测试 · ' + feedCaption(sample, options); slot.text.style.fontSize = options.fontSize + 'px';
            slot.text.text = i % 2 ? '先守住基地，等队友集合。' : '等我大招好了再一起推进，先别分散。';
        }
    }
    function maintainDisplayPreview() {
        if (!state.preview || !state.preview.active) return;
        if (Date.now() >= state.preview.until) { closeDisplayPreview(); return; }
        if (!valid(state.previewRoot)) drawDisplayPreview();
    }
    function ownHudLabelHidden(label) {
        if (!valid(label)) return false;
        try { return label.visible === false || label.style.visibility === 'collapse' || label.style.visibility === 'hidden' || zeroOpacity(label.style.opacity); } catch (_) { return false; }
    }
    function zeroOpacity(raw) {
        if (raw === undefined || raw === null || String(raw).trim() === '') return false;
        var value = Number(raw); return isFinite(value) && value <= 0.001;
    }
    function labelFontCurrent(label) {
        if (!valid(label)) return false;
        var raw = '';
        try { raw = String(label.style.fontSize || '').trim(); } catch (_) {}
        // Panorama can canonicalize 17px as 17.000px, or omit the getter while the
        // setter still works. String equality causes constant layout invalidation.
        if (/^\d+(?:\.\d+)?px$/i.test(raw)) return Math.abs(parseFloat(raw) - Number(state.config.fontSize)) < 0.01;
        return label.__dltFontSize === state.config.fontSize;
    }
    function updateLabel(label, value) {
        if (label.text !== value) label.text = value;
        if (label.__dltFontSize !== state.config.fontSize || !labelFontCurrent(label)) label.style.fontSize = state.config.fontSize + 'px';
        label.__dltFontSize = state.config.fontSize;
    }
    function hudLayoutEvidence(label, source, host, row) {
        function size(panel, key) {
            try { var value = panel[key]; return typeof value === 'number' && isFinite(value) && value >= -16384 && value <= 16384 ? Math.round(value * 10) / 10 : null; } catch (_) { return null; }
        }
        return { labelWidth: size(label, 'actuallayoutwidth'), labelHeight: size(label, 'actuallayoutheight'),
            sourceWidth: size(source, 'actuallayoutwidth'), sourceHeight: size(source, 'actuallayoutheight'),
            hostWidth: size(host, 'actuallayoutwidth'), hostHeight: size(host, 'actuallayoutheight'),
            rowWidth: size(row, 'actuallayoutwidth'), rowHeight: size(row, 'actuallayoutheight'),
            labelX: size(label, 'actualxoffset'), labelY: size(label, 'actualyoffset') };
    }
    function nativeRenderFlags(record, label) {
        // Fixed bits only; never store the read-back text. Readiness stays engine-owned.
        var flags = 0;
        try { if (typeof label.BReadyForDisplay === 'function') { var ready = label.BReadyForDisplay(); if (typeof ready === 'boolean') { flags |= 1; if (ready) flags |= 2; } } } catch (_) {}
        try { if (typeof label.BIsTransparent === 'function') { var transparent = label.BIsTransparent(); if (typeof transparent === 'boolean') { flags |= 4; if (transparent) flags |= 8; } } } catch (_) {}
        try { if (label.visible === false) flags |= 16; if (zeroOpacity(label.style.opacity)) flags |= 32; } catch (_) {}
        var value = text(label);
        if (value === nativeLabelText(record)) flags |= 128;
        else if (value === '') flags |= 256;
        return flags;
    }
    // The target-context experiment returned no usable receipt in the real game.
    // Use the label's plain-text property; never depend on an unverified script
    // context switch to reveal a translated label.
    function writeNativeHudLabel(label, record, clear) {
        var started = Date.now();
        try {
            if (!valid(label) || !has(label, 'DltNativeBubbleTemplate')) return 5;
            if (clear) {
                // Keep the label in the engine layout: the idle class reserves no
                // space, and a recycled row can use the first-pass path again.
                if (text(label) !== '') label.text = '';
                label.RemoveClass('DltNativeReady'); label.style.opacity = '0'; label.style.visibility = 'visible';
                return 2;
            }
            var host = label.GetParent(), row = valid(host) ? host.GetParent() : null;
            if (!valid(host) || host.id !== 'MessageContents' || !valid(row) || !has(row, 'ChatMessage')) return 5;
            if (has(row, 'Expired') || displayHidden(row)) return 3;
            var source = hudSource(row);
            if (source.host !== host || !valid(source.text) || displayHidden(source.text) || normalized(text(source.text)) !== record.original ||
                (record.nativeId && nativeMessageId(row) !== record.nativeId)) return 4;
            var value = nativeLabelText(record);
            label.html = false; label.hittest = false;
            if (text(label) !== value) label.text = value;
            if (text(label) !== value) return 8;
            if (label.__dltFontSize !== state.config.fontSize || !labelFontCurrent(label)) label.style.fontSize = state.config.fontSize + 'px';
            label.html = false; label.hittest = false; label.visible = true;
            label.AddClass('DltNativeReady'); label.style.visibility = 'visible'; label.style.opacity = String(appearance().opacity / 100);
            return 1;
        } catch (_) { return 6; }
        finally { if (valid(label)) label.__dltNativeWriteMs = Math.max(0, Date.now() - started); }
    }
    function nativeHudUpdateKey(record) { return [state.session, record.id, nativeLabelText(record), state.config.fontSize, appearance().opacity].join('\n'); }
    function nativeHudUpdateBlocked(label, record) {
        var previous = valid(label) ? label.__dltNativeAttempt : null;
        return !!(previous && previous.key === nativeHudUpdateKey(record) && (previous.failures >= 2 || (previous.attempts >= 2 && !ownHudLabelHidden(label))));
    }
    function publishNativeLocalUpdate(label, record) {
        // Optional handoff. A missing controller/attribute API never gates the
        // established direct renderer or keeps its label intentionally hidden.
        if (typeof label.SetAttributeString !== 'function') return '';
        var id = state.session + ':' + String(++state.nativeLocalSerial);
        try {
            label.SetAttributeString('dlt_local_command', JSON.stringify({ schema: 1, id: id, at: Date.now(),
                message: state.session + ':' + record.id, original: record.original, nativeId: record.nativeId || '', value: nativeLabelText(record),
                fontSize: state.config.fontSize, opacity: appearance().opacity }));
            return id;
        } catch (_) { return ''; }
    }
    function nativeLocalMissingStatus(label) {
        var panel = label, readable = false;
        for (var i = 0; i < 8 && valid(panel); i++) {
            try {
                if (typeof panel.GetAttributeString === 'function') {
                    readable = true;
                    if (panel.GetAttributeString('dlt_local_owner', '')) {
                        var stamp = Number(panel.GetAttributeString('dlt_local_tick', '')), age = Date.now() - stamp;
                        return stamp > 0 && isFinite(age) && age >= 0 && age <= 2000 ? 0 : 9;
                    }
                }
                panel = panel.GetParent();
            } catch (_) { break; }
        }
        return readable ? 8 : 10;
    }
    function nativeAncestry(label) {
        var result = [], panel = label;
        function number(p, key, opacity) {
            try { var value = opacity ? p.style.opacity : p[key];
                if (value === '' || value === undefined || value === null) return null;
                value = Number(value); return isFinite(value) && value >= (opacity ? 0 : -16384) && value <= (opacity ? 1 : 16384) ? Math.round(value * 100) / 100 : null;
            } catch (_) { return null; }
        }
        for (var level = 0; level < 8 && valid(panel); level++) {
            var role = panel === label ? 1 : panel.id === 'MessageContents' ? 2 : has(panel, 'ChatMessage') ? 3 : panel.id === 'Messages' ? 4 :
                panel.id === 'Team1Chat' || panel.id === 'Team2Chat' ? 5 : has(panel, 'ChatContainer') ? 6 : panel.id === 'TopBar' ? 7 : panel === ROOT ? 9 : 0;
            var flags = 0, overflow = 0;
            try { if (panel.visible === false) flags |= 1; if (panel.style.visibility === 'collapse' || panel.style.visibility === 'hidden') flags |= 2;
                if (has(panel, 'Expired')) flags |= 4; if (has(panel, 'gScoreboardOpen')) flags |= 8; if (has(panel, 'gDetailView')) flags |= 16;
                if (typeof panel.BReadyForDisplay === 'function') { flags |= 32; if (panel.BReadyForDisplay()) flags |= 64; }
                if (typeof panel.BIsTransparent === 'function') { flags |= 128; if (panel.BIsTransparent()) flags |= 256; }
                var raw = String(panel.style.overflow || '').trim(); overflow = raw === 'clip' ? 1 : raw === 'noclip' ? 2 : raw === 'scroll' ? 3 : 0;
            } catch (_) {}
            result.push({ level: level, role: role, width: number(panel, 'actuallayoutwidth'), height: number(panel, 'actuallayoutheight'),
                x: number(panel, 'actualxoffset'), y: number(panel, 'actualyoffset'), opacity: number(panel, '', true), flags: flags, overflow: overflow });
            try { panel = panel.GetParent(); } catch (_) { break; }
        }
        return result;
    }
    function nativeHudCurrent(label, record) {
        if (!valid(label) || !has(label, 'DltNativeBubbleTemplate') || label.__dltNativeValue !== nativeLabelText(record)) return false;
        return text(label) === nativeLabelText(record);
    }
    function resetNativeLayoutState(label) {
        // A recycled native row (or a cleared label) must be able to use the
        // first-pass layout path again. Only our own attributes are touched.
        try {
            if (typeof label.GetAttributeString !== 'function') return;
            var keys = ['dlt_native_nudge_after_w', 'dlt_native_nudge_after_h', 'dlt_native_nudge_done', 'dlt_native_nudge_state', 'dlt_native_nudge_at', 'dlt_native_nudge_w', 'dlt_native_nudge_h', 'dlt_native_nudge_elapsed', 'dlt_native_layout_at', 'dlt_native_birth', 'dlt_native_nudge', 'dlt_native_nudged',
                'dlt_native_reflow_done', 'dlt_native_reflow_flags', 'dlt_native_value', 'dlt_native_original', 'dlt_native_message',
                'dlt_native_row_gate', 'dlt_native_root_gate'];
            for (var i = 0; i < keys.length; i++) label.SetAttributeString(keys[i], '');
        } catch (_) {}
    }
    // The engine assigns layout offsets only to panels it has laid out. This
    // separates "already laid out and empty" from "never laid out", without
    // depending on a translation or changing any native panel.
    function nativeLabelPositioned(label) {
        if (!valid(label)) return false;
        try { return typeof label.actualxoffset === 'number' && typeof label.actualyoffset === 'number'; } catch (_) { return false; }
    }
    function clearNativeHudLabel(label) {
        if (!valid(label) || !has(label, 'DltNativeBubbleTemplate')) return;
        try { if (label.id === 'DltNativeText' && label.GetAttributeString('dlt_native_owned', '') !== '1') return; } catch (_) { return; }
        try { if (typeof label.SetAttributeString === 'function') label.SetAttributeString('dlt_local_command', ''); } catch (_) {}
        var result = writeNativeHudLabel(label, null, true);
        if (result !== 2) {
            // Clearing must hide the old value even if its native setter throws.
            try { label.style.opacity = '0'; } catch (_) {}
        }
        label.__dltNativeValue = ''; label.__dltNativeAttempt = null;
        resetNativeLayoutState(label);
    }
    function clearNativeHudBindings() {
        for (var i = 0; i < state.records.length; i++) for (var j = 0; j < state.records[i].views.length; j++) {
            var view = state.records[i].views[j]; if (view.kind === 'hud' && valid(view.row)) clearNativeHudLabel(view.row.__dltLabel);
        }
        state.nativeChecks = [];
    }
    function updateNativeHudLabel(record, view, host) {
        var label = null, fallback = null;
        for (var c = 0, n = Math.min(count(host), 16); c < n; c++) {
            var candidate = child(host, c); if (!valid(candidate) || !has(candidate, 'DltNativeBubbleTemplate')) continue;
            if (candidate.id === 'DltBubbleTranslation') fallback = candidate;
            try { if (candidate.id === 'DltNativeText' && candidate.GetAttributeString('dlt_native_owned', '') === '1') { label = candidate; break; } } catch (_) {}
        }
        if (!label) label = fallback;
        if (!valid(label) || !has(label, 'DltNativeBubbleTemplate') || !inside(label, host)) {
            waitForHud(record, view, 'template-missing'); return false;
        }
        var hadValue = !!label.__dltNativeValue, repairingHost = valid(view.row.__dltLabel) && view.row.__dltLabel !== label && view.row.__dltLabel.GetParent() !== host;
        var value = nativeLabelText(record), recoveringHidden = ownHudLabelHidden(label), changed = !nativeHudCurrent(label, record) || !labelFontCurrent(label) || recoveringHidden;
        if (valid(view.row.__dltLabel) && view.row.__dltLabel !== label) clearNativeHudLabel(view.row.__dltLabel);
        view.row.__dltLabel = label;
        if (changed) {
            var previous = label.__dltNativeAttempt, key = nativeHudUpdateKey(record);
            if (previous && previous.key === key && (previous.failures >= 2 || (!recoveringHidden && previous.attempts >= 2))) return false;
            label.__dltNativeAttempt = { key: key, attempts: previous && previous.key === key ? previous.attempts + 1 : 1,
                failures: previous && previous.key === key ? previous.failures : 0 };
            // First write only: was the native label already part of the engine
            // layout before any translation existed? count=1 means it was.
            if (!hadValue) {
                var preSource = hudSource(view.row);
                audit('hud-layout-before', record.id, null, null, nativeLabelPositioned(label) ? 1 : 0, hudLayoutEvidence(label, preSource.text, host, view.row));
            }
            var applied = writeNativeHudLabel(label, record, false);
            if (applied !== 1) {
                label.__dltNativeAttempt.failures++;
                if (view.writeFailure !== applied) { view.writeFailure = applied; audit('hud-native-write-failed', record.id, null, null, applied, null, nativeAncestry(label)); }
                return false;
            }
            label.__dltNativeAttempt.failures = 0;
            view.writeFailure = 0; label.__dltNativeValue = value; label.__dltFontSize = state.config.fontSize;
            if (label.id === 'DltNativeText' && !label.__dltBirthLogged) { label.__dltBirthLogged = true; audit('hud-native-born', record.id); }
            var localId = publishNativeLocalUpdate(label, record);
            audit('hud-direct-applied', record.id, null, null, localId ? 1 : 0, null, nativeAncestry(label), label.__dltNativeWriteMs);
            if (repairingHost) { state.metrics.hudHostRepairs++; audit('hud-host-repaired', record.id); }
            if (recoveringHidden && hadValue) { state.metrics.hudHiddenRepairs++; audit('hud-hidden-repaired', record.id); }
            audit('hud-template-bound', record.id);
            state.nativeChecks = state.nativeChecks.filter(function (entry) { return entry.view !== view; });
            if (state.nativeChecks.length >= 64) state.nativeChecks.shift();
            state.nativeChecks.push({ record: record, view: view, label: label, due: Date.now() + 200, revision: record.revision, sample: 1, localId: localId, localReceiptLogged: false });
        }
        state.metrics.hudRendered++;
        if (view.displayState) { state.metrics.hudRetryRecovered++; audit('hud-restored', record.id); }
        view.displayState = ''; record.hudRetryUntil = 0; audit('hud-written', record.id);
        return true;
    }
    function checkNativeHudTemplates() {
        if (!state.config.enabled || !nativeDisplay()) { state.nativeChecks = []; return; }
        for (var i = 0, countChecks = Math.min(2, state.nativeChecks.length); i < countChecks; i++) {
            var entry = state.nativeChecks.shift();
            if (state.recordsById[entry.record.id] !== entry.record || entry.record.revision !== entry.revision || !valid(entry.label)) continue;
            if (Date.now() < entry.due) { state.nativeChecks.push(entry); continue; }
            if (!valid(entry.view.row) || entry.view.row.__dltLabel !== entry.label || entry.view.row.__dltRecord !== entry.record || entry.view.row.__dltSession !== state.session) continue;
            var source = hudSource(entry.view.row), evidence = hudLayoutEvidence(entry.label, source.text, source.host, entry.view.row);
            if (entry.localId && !entry.localReceiptLogged) {
                var receipt = null;
                try { if (typeof entry.label.GetAttributeString === 'function') receipt = JSON.parse(entry.label.GetAttributeString('dlt_local_receipt', '')); } catch (_) {}
                if (receipt && receipt.id === entry.localId && typeof receipt.code === 'number' && receipt.code >= 1 && receipt.code <= 7 && receipt.code === Math.floor(receipt.code)) {
                    audit('hud-local-receipt', entry.record.id, null, null, receipt.code, null, null, receipt.ms); entry.localReceiptLogged = true;
                } else if (entry.sample === 2) audit('hud-local-receipt', entry.record.id, null, null, nativeLocalMissingStatus(entry.label));
            }
            audit('hud-template-layout', entry.record.id, null, null, entry.sample, evidence, nativeAncestry(entry.label));
            var nudgeState = 0;
            try { if (typeof entry.label.GetAttributeString === 'function') nudgeState = Number(entry.label.GetAttributeString('dlt_native_nudge_state', '0')); } catch (_) {}
            if (nudgeState > 0 && nudgeState < 5 && !entry.nudgeStarted) {
                entry.nudgeStarted = true;
                audit('hud-nudge-start', entry.record.id, null, null, 1, {
                    labelWidth: Number(entry.label.GetAttributeString('dlt_native_nudge_w', '0')),
                    labelHeight: Number(entry.label.GetAttributeString('dlt_native_nudge_h', '0')) });
            }
            if (nudgeState >= 3 && nudgeState !== entry.nudgeOutcome) {
                entry.nudgeOutcome = nudgeState;
                audit(nudgeState === 3 ? 'hud-nudge-recovered' : 'hud-nudge-failed', entry.record.id, null, null, nudgeState, { labelWidth: Number(entry.label.GetAttributeString('dlt_native_nudge_after_w', '0')), labelHeight: Number(entry.label.GetAttributeString('dlt_native_nudge_after_h', '0')) }, null,
                    Number(entry.label.GetAttributeString('dlt_native_nudge_elapsed', '0')));
            }

            try {
                var reflow = Number(entry.label.GetAttributeString('dlt_native_reflow_flags', '0'));
                if (reflow > 0 && reflow <= 31 && reflow !== entry.reflowLogged) {
                    entry.reflowLogged = reflow;
                    audit('hud-root-reflow', entry.record.id, null, null, reflow, evidence);
                }
            } catch (_) {}
            if (entry.sample === 1) audit('hud-template-render-state', entry.record.id, null, null, nativeRenderFlags(entry.record, entry.label));
            if ((evidence.labelWidth === 0 || evidence.labelHeight === 0) && evidence.sourceWidth > 0 && evidence.sourceHeight > 0) audit('hud-layout-stalled', entry.record.id, null, null, null, evidence);
            if ((entry.sample === 1 || nudgeState > 0 && nudgeState < 3 && entry.sample < 4) && !has(entry.view.row, 'Expired')) { entry.sample++; entry.due = Date.now() + (entry.sample === 2 ? 800 : 400); state.nativeChecks.push(entry); }
        }
    }
    function renderOne(record) {
        record.renderQueued = false;
        if (!state.config.enabled) return;
        if (!showRecord(record)) { hideOwnRecord(record); return; }
        var value = record.translation; if (!value) return;
        if (independentDisplay()) renderFeed(record);
        for (var i = 0; i < record.views.length; i++) {
            var view = record.views[i], row = view.row;
            if (view.kind === 'hud' && !nativeDisplay()) continue;
            if (view.kind === 'hud') {
                var reason = nativeDisplayState(view, record);
                if (reason === 'panel-gone' || reason === 'native-expired' || reason === 'message-changed') {
                    if (view.displayState !== reason) {
                        view.displayState = reason;
                        if (reason !== 'message-changed') { state.metrics.hudExpiredResults++; state.metrics[reason === 'native-expired' ? 'hudNativeExpired' : 'hudPanelGone']++; }
                        audit('hud-retired', record.id, null, reason);
                    }
                    record.hudRetryUntil = 0; continue;
                }
                if (reason) { waitForHud(record, view, reason); continue; }
            }
            if (!valid(row) || row.__dltFingerprint !== view.fingerprint || row.__dltRecord !== record || row.__dltSession !== state.session) continue;
            if (rowText(row, view.kind, find(row, 'MessageContents')) !== record.original) continue;
            var host;
            if (view.kind === 'hud') host = hudSource(row).host;
            else if (view.kind === 'lobby') {
                // Keep every engine-owned child under its original parent and in its original order.
                row.AddClass('DltLobbyTranslationRow');
                host = row;
            } else host = classes(row, 'MessageBody')[0] || row;
            if (!valid(host)) continue;
            if (view.kind === 'hud') { updateNativeHudLabel(record, view, host); continue; }
            var label = row.__dltLabel;
            if (!valid(label) || !inside(label, host)) {
                // Retired native contents can remain valid descendants of the message. Clear
                // only our obsolete label before creating one under the current contents.
                if (valid(label)) label.text = '';
                label = $.CreatePanel('Label', host, 'DltTranslation');
                label.AddClass('DltTranslation');
                label.html = false; label.hittest = false; row.__dltLabel = label;
                // Visuals live in CSS so history and HUD share the game palette.
            }
            updateLabel(label, nativeLabelText(record)); label.visible = true; label.style.opacity = String(appearance().opacity / 100);
        }
        state.metrics.rendered++;
    }
    function receive(payload) {
        if (payload.session && payload.session !== state.session) return false;
        if (payload.error) { audit('bridge-error'); state.metrics.errors++; try { $.Msg('[DLT] bridge response error (no message content logged)'); } catch (_) {} return false; }
        if (!state.pending || payload.seq !== state.pending.seq) return false;
        var editorSent = state.pending.nativeEditor;
        var editorServerChanged = payload.serverId && state.serverId && payload.serverId !== state.serverId;
        state.lastReplyAt = Date.now();
        var accepted = state.pending.messages || [];
        for (var a = 0; a < accepted.length; a++) { for (var q = 0; q < state.outbox.length; q++) if (state.outbox[q].id === accepted[a].id) { state.outbox.splice(q, 1); break; } }
        state.metrics.submitted += accepted.length;
        var acceptedOutgoing = state.pending.outgoing || [];
        for (var o = 0; o < acceptedOutgoing.length; o++) {
            var admitted = state.outgoingKnown[acceptedOutgoing[o].id]; if (admitted) admitted.registrationAcked = true;
            state.outgoingQueue = state.outgoingQueue.filter(function (item) { return item.id !== acceptedOutgoing[o].id; });
        }
        var acknowledged = state.pending.outgoingSent || [];
        state.outgoingSent = state.outgoingSent.filter(function (id) { return acknowledged.indexOf(id) < 0; });
        var partAcknowledged = state.pending.outgoingPartAcks || [];
        state.outgoingPartAcks = state.outgoingPartAcks.filter(function (item) { return !partAcknowledged.some(function (ack) { return ack.id === item.id && ack.part === item.part; }); });
        var statusAcknowledged = state.pending.outgoingStatus || [];
        state.outgoingStatus = state.outgoingStatus.filter(function (item) { return !statusAcknowledged.some(function (ack) { return ack.seq === item.seq; }); });
        if (state.pending.refused) state.outgoingRefused = [];
        state.pending = null; state.online = true;
        if (state.config.transport === 'fragment' && !state.autoFallback) state.fragmentWorked = true;
        // A newly created chat adapter cannot inherit a keypress from the previous scene.
        if (payload.serverId && !state.serverId) state.hotkeySeen = payload.hotkey || 0;
        if (payload.serverId && state.serverId && payload.serverId !== state.serverId) {
            state.outgoingQueue = []; state.outgoingSent = []; state.outgoingPartAcks = []; state.outgoingKnown = {}; state.nextSendAt = 0;
            state.pendingHotkey = null; state.closeWatch = null; state.outgoingRefused = [];
            state.echoes = []; state.outgoingStatus = []; state.channelFault = ''; state.recentOwn = [];
            state.hotkeySeen = payload.hotkey || 0;
            audit('bridge-reset');
            for (var s = 0; s < state.records.length; s++) {
                var retry = state.records[s];
                if (!retry.done && Date.now() - retry.created < 30000 && state.outbox.length < 64 && !state.outbox.some(function (m) { return m.id === retry.id; }))
                    state.outbox.push({ id: retry.id, text: retry.original, sender: retry.sender, channel: retry.channel });
            }
        }
        if (payload.serverId) state.serverId = payload.serverId;
        var editorCurrent = ROOT.__dltNativeEditorCommand, editorResult = payload.nativeEditor;
        if (editorSent && editorCurrent && editorSent.id === editorCurrent.id && editorSent.request === editorCurrent.request) {
            if (editorServerChanged) editorResult = { id: editorSent.id, request: editorSent.request, state: 'error', message: '后台已重新启动，请保留当前文字，取消后重新编辑。' };
            else if (!editorResult && payload.capabilities && payload.capabilities.indexOf('native-settings-editor') < 0) editorResult = { id: editorSent.id, request: editorSent.request, state: 'error', message: '后台尚不支持原生输入，请成套更新后重试。' };
            if (editorResult && editorResult.id === editorSent.id && editorResult.request === editorSent.request) {
                ROOT.__dltNativeEditorReply = editorResult; ROOT.__dltNativeEditorCommand = null;
            }
        }
        if (payload.config) {
            state.config = payload.config;
            ROOT.__dltSettingsBackgroundMode = state.config.settingsBackgroundMode || 'static';
            if (typeof state.config.fontSize !== 'number' || !isFinite(state.config.fontSize) || state.config.fontSize < 12 || state.config.fontSize > 24) state.config.fontSize = 17;
            syncOwnDisplay(); maintainFeed();
        }
        if (payload.displayPreview) acceptDisplayPreview(payload.displayPreview);
        if (!state.config.incomingScope) state.config.incomingScope = 'all_players';
        if (!state.config.incomingTargetLanguage) state.config.incomingTargetLanguage = 'zh-Hans';
        if (!state.config.outgoingTargetLanguage) state.config.outgoingTargetLanguage = 'en';
        var capabilities = payload.capabilities;
        state.partyAvailable = !!capabilities && capabilities.indexOf('outgoing-party-channel') >= 0;
        state.multiLanguageAvailable = !!capabilities && capabilities.indexOf('multilingual-targets') >= 0;
        state.statusSupported = !!capabilities && capabilities.indexOf('outgoing-status-v1') >= 0;
        state.diagSupported = !!capabilities && capabilities.indexOf('diagnostic-logs') >= 0;
        if (typeof payload.logAck === 'number') state.logs = state.logs.filter(function (event) { return event.seq > payload.logAck; });
        else if (state.serverId && !state.diagSupported) state.logs = [];
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
        // Cache bridge results only. Native close/focus changes must settle in
        // scheduled game callbacks before any text is handed to the controller.
        cacheOutgoing(payload.outgoing || []);
        var results = payload.results || [];
        for (var i = 0; i < results.length; i++) {
            var result = results[i];
            var record = state.recordsById[result.id];
            if (!record) continue;
            record.done = record.done || result.done;
            if (result.revision > record.revision && result.text) {
                record.revision = result.revision; record.translation = result.text; record.stage = result.stage; queueRender(record);
            }
        }
        var progress = payload.translationProgress || {}, working = '';
        ROOT.__dltTranslationProgress = progress;
        if (progress.thinking) working += ' · 思考 ' + progress.thinking;
        if (progress.generating) working += ' · 生成 ' + progress.generating;
        if (progress.waiting) working += ' · 等待 ' + progress.waiting;
        status(state.config.enabled ? (payload.configuredLlm ? '翻译已连接' + working : '快译已连接 · 精翻未配置') : '翻译已暂停');
        return true;
    }
    function resetTransport() {
        state.pending = null; state.initialized = false; state.online = false;
        state.eventTitle = ''; state.lastTitle = ''; state.titleSamples = []; state.nextConnect = 0; state.nextNavigation = 0;
        // Outbox, message IDs, results and native-send acknowledgements are retained.
    }
    function ensureBridge() {
        if (!valid(state.bridge) || !inside(state.bridge, context)) {
            unbindBridge(); state.bridge = find(context, 'DltBridge');
            if (!valid(state.bridge) && Date.now() >= state.nextBridgeCreate) {
                state.nextBridgeCreate = Date.now() + 5000;
                try {
                    state.bridge = $.CreatePanel('HTML', context, 'DltBridge');
                    state.bridge.AddClass('DltBridge'); state.bridge.hittest = false;
                    state.bridge.style.width = '1px'; state.bridge.style.height = '1px'; state.bridge.style.opacity = '0'; state.bridge.style.position = '-10px -10px 0px';
                    audit('bridge-rebuilt');
                } catch (_) { state.bridge = null; state.metrics.errors++; }
            }
        }
        if (valid(state.bridge) && boundBridge !== state.bridge) {
            unbindBridge(); boundBridge = state.bridge; resetTransport(); initialLoads = 0;
            // This browser only transports data; it must never own keyboard input
            // while the separate settings HTML surface is being edited.
            try { if (state.bridge.SetAcceptsInput) state.bridge.SetAcceptsInput(false); } catch (_) {}
            try { if (state.bridge.SetAcceptsFocus) state.bridge.SetAcceptsFocus(false); } catch (_) {}
            for (var e = 0; e < titleEvents.length; e++) { try { $.RegisterEventHandler(titleEvents[e], state.bridge, titleEvent); } catch (_) {} }
            audit('bridge-bound');
        }
        return valid(state.bridge);
    }
    function bridgeTick() {
        var now = Date.now();
        if (!ensureBridge()) return;
        var event = state.eventTitle || ''; state.eventTitle = '';
        for (var t = 0; t < 4; t++) {
            // Consult fallbacks only while a handshake/reply is actually outstanding.
            if (t >= 2 && state.initialized && !state.pending) break;
            var title = '';
            try {
                if (t === 0) title = event;
                else if (t === 1) title = String(state.bridge.title || '');
                else if (t === 2 && state.bridge.GetTitle) title = String(state.bridge.GetTitle() || '');
                else if (t === 3 && state.bridge.GetAttributeString) title = String(state.bridge.GetAttributeString('title', '') || '');
            } catch (_) {}
            if (state.titleSamples[t] === title) continue; state.titleSamples[t] = title;
            if (title.indexOf('DLT:') !== 0 || title === state.lastTitle) continue;
            if (title === 'DLT:ready') {
                if (!state.initialized) { state.initialized = true; state.nextConnect = 0; initialLoads = 0; ROOT.__dltLoopbackHost = loopbackHost; }
            } else if (state.pending) {
                try { if (receive(JSON.parse(title.slice(4)))) { state.lastTitle = title; state.initialized = true; } } catch (_) { state.metrics.errors++; }
            }
        }
        if (state.pending && now - state.pending.at > 4500) {
            if (state.config.transport === 'fragment' && !state.fragmentWorked) { state.config.transport = 'navigation'; state.autoFallback = true; }
            audit('bridge-timeout'); state.metrics.errors++; state.pending = null; state.initialized = false; state.online = false;
            state.nextConnect = now + 5000; status('后台未连接 · 请运行启动器');
        }
        if (state.pending || now < state.nextConnect) return;
        if (!state.initialized) {
            // Retry only the two SAN-covered loopback names; keep HTTPS and pairing intact.
            if (initialLoads > 0) {
                loopbackHost = loopbackHost === 'localhost' ? '127.0.0.1' : 'localhost';
                base = 'https://' + loopbackHost + ':17866/bridge/__PAIRING__';
                audit('transport-fallback');
            }
            initialLoads++;
            state.lastTitle = ''; state.titleSamples = [];
            state.eventTitle = '';
            if (!state.loggedBridge) { state.loggedBridge = true; try { $.Msg('[DLT] loading localhost bridge; GetTitle=' + String(typeof state.bridge.GetTitle)); } catch (_) {} }
            state.bridge.SetURL(base); state.metrics.bridges++; state.nextConnect = now + 5000;
            // initial load has a timeout as well; do not busy-loop when localhost is offline
            return;
        }
        // Full-document navigation is a compatibility fallback, not an 80 ms polling channel.
        // This cap also applies to queued diagnostics and outgoing bursts.
        if (state.config.transport === 'navigation' && now < state.nextNavigation) return;
        var ids = [], pendingRecords = [], liveBubbles = [];
        for (var i = 0; i < state.records.length; i++) if (!state.records[i].done) {
            if (now - state.records[i].created > 90000) state.records[i].done = true;
            else { pendingRecords.push(state.records[i]); if (hasLiveBubble(state.records[i])) liveBubbles.push(state.records[i]); }
        }
        // Reserve half the response budget for live bubbles. Rotate both groups so a
        // slow first batch can never starve newer messages or the remaining history.
        for (var b = 0; b < Math.min(24, liveBubbles.length); b++) ids.push(liveBubbles[(state.hudPollCursor + b) % liveBubbles.length].id);
        for (var p = 0; p < pendingRecords.length && ids.length < 48; p++) {
            var id = pendingRecords[(state.pollCursor + p) % pendingRecords.length].id;
            if (ids.indexOf(id) < 0) ids.push(id);
        }
        var wantMetrics = now >= state.nextMetrics, wantHeartbeat = now >= state.nextHeartbeat;
        var chatOpen = inputOpen();
        var outgoingPending = Object.keys(state.outgoingKnown).some(function (id) { return !state.outgoingKnown[id].done; });
        var waiting = [];
        for (var k in state.outgoingKnown) if (state.outgoingKnown[k].waiting && !state.outgoingKnown[k].done) waiting.push(k);
        var pendingLogs = state.diagSupported && state.logs.length > 0;
        var settingsWindow = ROOT.__dltActiveSettingsWindow;
        var settingsOpen = valid(settingsWindow) && !settingsWindow.__dltClosing;
        var editorSource = ROOT.__dltNativeEditorCommand, editorCommand = null;
        if (!settingsOpen) { ROOT.__dltNativeEditorCommand = null; ROOT.__dltNativeEditorReply = null; editorSource = null; }
        if (settingsOpen && editorSource && /^[a-f0-9]{48}$/.test(String(editorSource.id || '')) && typeof editorSource.request === 'number' && editorSource.request >= 1 && editorSource.request <= 1000000000 && ['claim', 'apply', 'cancel'].indexOf(editorSource.action) >= 0) {
            editorCommand = { id: editorSource.id, action: editorSource.action, request: editorSource.request };
            if (editorSource.action === 'apply') {
                if (typeof editorSource.value === 'string' && editorSource.value.length <= 4096) editorCommand.value = editorSource.value;
                else { ROOT.__dltNativeEditorReply = { id: editorSource.id, request: editorSource.request, state: 'error', message: '编辑内容过长或无效，请缩短后重试。' }; ROOT.__dltNativeEditorCommand = null; editorCommand = null; }
            }
        }
        var inputEvidence = ROOT.__dltSettingsInputEvidence;
        if (settingsOpen && inputEvidence && state.settingsInputRevision !== inputEvidence.revision) {
            state.settingsInputRevision = inputEvidence.revision;
            audit('settings-input', null, null, null, inputEvidence.flags);
        }
        var backgroundEvidence = ROOT.__dltSettingsBackgroundEvidence;
        if (settingsOpen && backgroundEvidence && state.settingsBackgroundRevision !== backgroundEvidence.revision) {
            state.settingsBackgroundRevision = backgroundEvidence.revision;
            audit('settings-background', null, null, null, backgroundEvidence.flags);
        }
        if (state.settingsWasOpen !== settingsOpen) { state.settingsWasOpen = settingsOpen; state.forcePoll = true; }
        if (!editorCommand && !pendingLogs && !state.outbox.length && !state.outgoingQueue.length && !state.outgoingSent.length && !state.outgoingPartAcks.length && !(state.statusSupported && state.outgoingStatus.length) && !state.outgoingRefused.length && !state.openSettings && !wantMetrics && !wantHeartbeat && !state.forcePoll && (!(ids.length || chatOpen || outgoingPending || settingsOpen || state.pendingHotkey) || now < state.nextPoll)) return;
        state.forcePoll = false;
        var messages = state.outbox.slice(0, 8);
        var payload = { clientVersion: '0.6.25', clientBuildId: BUILD_ID, multipartSupported: true, displayPreviewSupported: true, settingsBackgroundSupported: true, session: state.session, seq: ++state.seq, messages: messages, poll: ids,
            openSettings: state.openSettings, metrics: wantMetrics ? state.metrics : null,
            chatOpen: chatOpen, chatChannel: nativeChannel(), settingsOpen: settingsOpen, hotkeySeen: state.hotkeySeen, outgoing: state.outgoingQueue.slice(0, 3),
            outgoingSent: state.outgoingSent.slice(0, 12), outgoingPartAcks: state.outgoingPartAcks.slice(0, 12), outgoingWaiting: waiting.slice(0, 12),
            outgoingStatus: state.statusSupported ? state.outgoingStatus.slice(0, 24) : undefined, outgoingRefused: state.outgoingRefused.slice(0, 12), serverId: state.serverId || '', logs: state.diagSupported || !state.serverId ? state.logs.slice(0, 8) : [] };
        if (editorCommand) {
            // Reuse the already initialised browser/HTTPS channel. Editor text is
            // isolated from incoming/outgoing chat and is never a translation job.
            payload.nativeEditor = editorCommand; payload.messages = []; payload.poll = []; payload.outgoing = []; payload.logs = []; payload.metrics = null;
        }
        var encoded = encodeURIComponent(JSON.stringify(payload));
        while (encoded.length > 6000 && payload.logs.length) { payload.logs.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        while (encoded.length > 6000 && payload.messages.length > 1) { payload.messages.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        while (encoded.length > 6000 && payload.outgoing.length > 1) { payload.outgoing.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        if (encoded.length > 6000 && payload.outgoing.length && payload.messages.length) { payload.messages = []; encoded = encodeURIComponent(JSON.stringify(payload)); }
        if (payload.outgoingStatus) {
            // Never acknowledge a release whose registration was removed by packet
            // budgeting or a settings-editor command. Keep it queued for the retry.
            payload.outgoingStatus = payload.outgoingStatus.filter(function (item) {
                var job = state.outgoingKnown[item.id];
                return job && job.registrationAcked || payload.outgoing.some(function (input) { return input.id === item.id; });
            });
            encoded = encodeURIComponent(JSON.stringify(payload));
            while (encoded.length > 6000 && payload.outgoingStatus.length > 1) { payload.outgoingStatus.pop(); encoded = encodeURIComponent(JSON.stringify(payload)); }
        }
        if (editorCommand && encoded.length > 24000) {
            ROOT.__dltNativeEditorReply = { id: editorCommand.id, request: editorCommand.request, state: 'error', message: '文本超出当前游戏通信长度，请缩短内容或在浏览器设置中编辑。' };
            ROOT.__dltNativeEditorCommand = null; return;
        }
        if (liveBubbles.length) state.hudPollCursor = (state.hudPollCursor + 24) % liveBubbles.length;
        if (pendingRecords.length) state.pollCursor = (state.pollCursor + 24) % pendingRecords.length;
        state.pending = { seq: payload.seq, at: now, messages: payload.messages, outgoing: payload.outgoing, outgoingSent: payload.outgoingSent, outgoingPartAcks: payload.outgoingPartAcks, outgoingStatus: payload.outgoingStatus, refused: payload.outgoingRefused.length > 0, nativeEditor: editorCommand ? { id: editorCommand.id, request: editorCommand.request } : null };
        state.openSettings = false; state.nextPoll = now + (chatOpen ? 80 : state.config.transport === 'navigation' ? 800 : 250);
        state.nextHeartbeat = now + (state.config.quickMessagesEnabled ? 500 : 2000);
        if (wantMetrics) state.nextMetrics = now + 10000;
        state.bridge.SetURL(base + (state.config.transport === 'navigation' ? '?d=' : '#') + encoded);
        if (state.config.transport === 'navigation') state.nextNavigation = now + 800;
        state.metrics.bridges++;
    }
    DLTOpenSettings = function () { state.openSettings = true; state.nextConnect = 0; if (!state.initialized) status('正在连接后台 · 未启动时请运行 Settings.cmd'); };
    function checkSession() {
        var currentRoot = rootOf(context);
        if (valid(currentRoot) && currentRoot !== ROOT) { dispose(); initialize('root-moved'); return false; }
        var identity = '', identityKnown = false;
        try { if (typeof Game !== 'undefined' && Game.GetMatchID) { var match = String(Game.GetMatchID()); if (/^(0|[1-9][0-9]*)$/.test(match)) { identity = match; identityKnown = true; } } } catch (_) {}
        var newChat = find(context, 'ChatMessages');
        var changed = state.chat && valid(newChat) && newChat !== state.chat;
        // Empty chat history is not a new match; native UI may expire or clear its rows.
        if (identityKnown && state.matchIdentity !== undefined && identity !== state.matchIdentity) changed = true;
        if (changed) {
            // Old HUDs may stay attached across a match change. Do not replay their
            // unchanged rows as new independent notifications in the new session.
            for (var old = 0; old < state.records.length; old++) for (var v = 0; v < state.records[old].views.length; v++) {
                var retired = state.records[old].views[v];
                if (retired.kind === 'hud' && valid(retired.row)) retired.row.__dltRetiredFeedFingerprint = retired.fingerprint;
            }
            state.session = String(Date.now()) + '-' + String(Math.random()).slice(2);
            state.logs = []; state.logSeq = 0; audit('session-reset');
            closeDisplayPreview(); clearFeed(false); clearNativeHudBindings(); state.hudRetries = []; state.records = []; state.recordsById = Object.create(null); state.outbox = []; state.render = []; state.pending = null; state.recentOwn = [];
            state.outgoingQueue = []; state.outgoingSent = []; state.outgoingPartAcks = []; state.outgoingKnown = {}; state.nextSendAt = 0;
            state.hotkeySeen = Math.max(state.hotkeySeen, state.pendingHotkey ? state.pendingHotkey.seq : 0);
            state.echoes = []; state.outgoingStatus = []; state.channelFault = ''; state.outgoingRefused = [];
            state.pendingHotkey = null; state.closeWatch = null; state.nativeClosedAt = 0; state.forcePoll = true;
            state.chat = newChat; state.chats = []; state.hud = []; state.hudRoots = []; state.extraHudRoots = []; state.hudSearch = null; state.pollCursor = state.hudPollCursor = 0;
            state.rootChat = state.rootMessages = state.lobby = state.input = state.targetLabel = null;
            state.chatTarget.panel = null; state.chatTarget.readable = false;
            state.heroRoster = []; state.heroTeams = []; state.nextHeroRoster = 0; state.heroRosterCount = -1;
            state.nextDiscovery = 0; state.nextMenuProbe = 0; state.menuProbeMisses = 0; state.discoveryWalk = null; state.discoveryIndex = {}; state.nextScan = 0; state.nextPoll = 0; state.resumeAt = Date.now() + 500;
            // Keep an already working HTML document; a scene transition is not a browser reload.
        }
        if (identityKnown) state.matchIdentity = identity;
    }
    function tick() {
        scheduled = null;
        if (stopped) return;
        if (!valid(context)) { dispose(); return; }
        if (!valid(ROOT)) { dispose(); initialize('root-moved'); return; }
        if (ROOT.__dltOwner !== context) { dispose(); return; }
        var started = Date.now(); lastTickAt = started;
        try {
            if (started >= state.nextSceneCheck) { if (checkSession() === false) return; state.nextSceneCheck = started + 200; }
            if (started < state.resumeAt) { armTick(0.1); return; }
            if (started >= state.nextDiscovery || !inside(state.menuMount, ROOT) && started >= state.nextMenuProbe) measureStage('discoveryMaxMs', discover);
            if (started >= state.nextScan) {
                measureStage('readMaxMs', function () {
                    try {
                        state.readBudget = { left: 24, until: Date.now()+2 }; scanBubbles();
                        state.readBudget = { left: 24, until: Date.now()+2 }; scanChat();
                    } finally { state.readBudget = null; }
                }); state.nextScan = started + state.config.scanIntervalMs;
            }
            // A chord whose chat box was not open yet is retried here until its window expires.
            if (state.pendingHotkey) captureOutgoing();
            state.turn++; watchClose();
            expireEchoes(); processOutgoing();
            var open = inputOpen();
            if (open !== state.lastOpenReported) { state.lastOpenReported = open; state.forcePoll = true; audit(open ? 'chat-open' : 'chat-closed'); }
            // At most two records update in one scheduled callback. No streaming or animations.
            measureStage('renderMaxMs', function () {
                for (var r = 0; r < 2 && state.render.length; r++) {
                    if (r > 0 && Date.now() - started >= 4) { performanceBucket().deferredWork++; break; }
                    renderOne(nextRender(r === 0));
                }
                checkNativeHudTemplates();
            });
            measureStage('bridgeMaxMs', bridgeTick);
            while (state.records.length > 300) { var expiredRecord = state.records.shift(); delete state.recordsById[expiredRecord.id]; }
        } catch (error) { state.metrics.errors++; if (!state.lastErrorAt || started - state.lastErrorAt > 10000) { state.lastErrorAt = started; try { $.Msg('[DLT] adapter callback failed; see diagnostic counters'); } catch (_) {} } }
        finishPerformance(Date.now() - started);
        state.metrics.scans++; state.metrics.maxScanMs = Math.max(state.metrics.maxScanMs, Date.now() - started);
        if (!stopped) armTick(inputOpen() ? 0.05 : state.closeWatch || hasPendingOutgoing() ? 0.1 : Math.max(0.1, Math.min(1, state.config.scanIntervalMs / 1000)));
    }
    function performanceBucket() {
        var now = Date.now(), slot = Math.floor(now / 5000), buckets = state.performanceBuckets;
        while (buckets.length && buckets[0].slot < slot - 5) buckets.shift();
        if (!buckets.length || buckets[buckets.length - 1].slot !== slot) buckets.push({ slot: slot, discoveryMaxMs: 0, readMaxMs: 0, renderMaxMs: 0, bridgeMaxMs: 0, windowMaxMs: 0, windowSamples: 0, slowCallbacks: 0, deferredWork: 0 });
        return buckets[buckets.length - 1];
    }
    function measureStage(name, fn) {
        var at = Date.now();
        try { return fn(); } finally { var bucket = performanceBucket(); bucket[name] = Math.max(bucket[name], Date.now() - at); }
    }
    function finishPerformance(elapsed) {
        var bucket = performanceBucket(); bucket.windowMaxMs = Math.max(bucket.windowMaxMs, elapsed); bucket.windowSamples++;
        if (elapsed >= 8) bucket.slowCallbacks++;
        var result = { discoveryMaxMs: 0, readMaxMs: 0, renderMaxMs: 0, bridgeMaxMs: 0, windowMaxMs: 0, windowSamples: 0, slowCallbacks: 0, deferredWork: 0 };
        for (var i = 0; i < state.performanceBuckets.length; i++) for (var key in result) {
            var value = state.performanceBuckets[i][key]; result[key] = /Ms$/.test(key) ? Math.max(result[key], value) : result[key] + value;
        }
        state.metrics.performance = result;
    }
    function armTick(delay) {
        var generation = loopGeneration;
        scheduled = $.Schedule(delay, function () { if (!stopped && generation === loopGeneration) tick(); });
    }
    armTick(0.3);
}());
