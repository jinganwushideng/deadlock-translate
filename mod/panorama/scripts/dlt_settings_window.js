var DLTSettingsClose = function () {};
var DLTSettingsRetry = function () {};
var DLTSettingsFocus = function () {};
var DLTEditorApply = function () {};
var DLTEditorCancel = function () {};
var DLTEditorBlur = function () {};
// A click on the window/frame is handled here, never as a backdrop-close gesture.
var DLTSettingsKeepOpen = function () { return true; };
(function () {
    'use strict';
    var context = $.GetContextPanel(), generation = 0, closed = false, observedEscape = false, hiddenTicks = 0;
    if (!valid(context)) return;
    var root = context;
    for (var depth = 0; depth < 32 && parent(root); depth++) root = parent(root);
    var host = null, ancestor = context, hostVisits = 0;
    while (valid(ancestor) && hostVisits++ < 32) {
        if (ancestor.id === 'DltSettingsWindowHost' && ancestor.__dltSettingsHost === true) { host = ancestor; break; }
        ancestor = parent(ancestor);
    }
    if (!valid(host) && valid(root.__dltActiveSettingsHost) && root.__dltActiveSettingsHost.__dltSettingsHost === true) host = root.__dltActiveSettingsHost;
    if (!valid(host)) { try { $.Msg('[DLT] settings host not verified; native panels preserved'); } catch (_) {} return; }
    var hostRoot = host;
    for (var depth = 0; depth < 32 && parent(hostRoot); depth++) hostRoot = parent(hostRoot);
    if (hostRoot !== root) return;
    // Layout execution context may be an ancestor. All window operations target only
    // the explicitly marked host created by dlt_menu, never an arbitrary native context.
    context = host;
    var page = context.FindChildTraverse('DltSettingsPage');
    root.__dltActiveSettingsHost = host; root.__dltActiveSettingsWindow = context;
    var backgroundMode = '', unsavedSettings = false, sceneLease = null, surfaceProbeUntil = 0, consumedProbeTitle = '', probeOpacity = null, backdropPainted = false;
    var backdrop = context.FindChildTraverse('DltWindowBackdrop');
    // Source 2 forbids IDs on a layout root. The parent of our known backdrop is
    // the actual stylesheet root, independent of the script execution context.
    var layoutRoot = valid(backdrop) ? parent(backdrop) : null;
    var probeInfo = context.FindChildTraverse('DltSurfaceProbeInfo'), probeText = context.FindChildTraverse('DltSurfaceProbeText');
    function probePageHidden(hidden) {
        if (!valid(page) || !page.style) return;
        if (hidden && !probeOpacity) {
            try {
                var previous = page.style.opacity == null ? '' : String(page.style.opacity);
                if (!previous && !page.ClearPropertyFromCode) return;
                page.style.opacity = '0'; probeOpacity = { previous: previous, applied: String(page.style.opacity) };
            } catch (_) {}
        } else if (!hidden && probeOpacity) {
            var lease = probeOpacity; probeOpacity = null;
            try {
                if (isFinite(Number(page.style.opacity)) && Number(page.style.opacity) === Number(lease.applied)) {
                    if (lease.previous) page.style.opacity = lease.previous;
                    else if (page.ClearPropertyFromCode) page.ClearPropertyFromCode('opacity');
                }
            } catch (_) {}
        }
    }
    function updateProbe() {
        var hidden = !!surfaceProbeUntil && Date.now() >= surfaceProbeUntil - 3000;
        probePageHidden(hidden);
        if (valid(probeInfo) && probeInfo.SetHasClass) probeInfo.SetHasClass('DltProbeActive', !!surfaceProbeUntil);
        if (surfaceProbeUntil && valid(probeText)) {
            var message = hidden ? (probeOpacity ? '透景检查：网页暂时隐藏，绿底应完整显示；稍后自动恢复。' : '透景检查：未能隐藏网页，本阶段无效；稍后自动恢复。') : '透景检查：空白处应透出绿色底板；稍后会隐藏网页，再自动恢复。';
            if (probeText.text !== message) probeText.text = message;
        }
        backgroundEvidence();
    }
    function restoreScene() {
        if (!sceneLease) return;
        var lease = sceneLease; sceneLease = null;
        if (!valid(lease.owner) || lease.owner.__dltSceneLease !== lease) return;
        for (var i = lease.changes.length - 1; i >= 0; i--) {
            var change = lease.changes[i]; if (!valid(change.panel)) continue;
            try {
                if (change.kind === 'visible') { if (change.panel.visible === false) change.panel.visible = change.previous; }
                else if (String(change.panel.style.backgroundColor || '') === change.applied) {
                    if (change.previous) change.panel.style.backgroundColor = change.previous;
                    else if (change.panel.ClearPropertyFromCode) change.panel.ClearPropertyFromCode('background-color');
                }
            } catch (_) {}
        }
        lease.owner.__dltSceneLease = null;
    }
    function exposeScene() {
        if (sceneLease) return;
        var owner = parent(host);
        if (!valid(owner) || !owner.FindChildTraverse || !owner.style || owner.__dltSceneLease) return;
        // Only the verified Escape menu that owns this host may be changed. Never
        // clear backgrounds or visibility on an arbitrary ancestor / QOL root.
        if (!valid(owner.FindChildTraverse('EscapeBackground')) || !valid(owner.FindChildTraverse('Menu'))) return;
        var lease = { owner: owner, changes: [] }; owner.__dltSceneLease = lease; sceneLease = lease;
        try {
            var before = String(owner.style.backgroundColor || '');
            if (before || owner.ClearPropertyFromCode) {
                owner.style.backgroundColor = '#00000000';
                lease.changes.push({ panel: owner, kind: 'background', previous: before, applied: String(owner.style.backgroundColor || '') });
            }
        } catch (_) {}
        var ids = ['LeftStripe', 'RightSide', 'LeftStripeBlur', 'RightSideBlur'];
        for (var i = 0; i < ids.length; i++) {
            var panel = owner.FindChildTraverse(ids[i]); if (!valid(panel)) continue;
            var containsHost = false, ancestor = host;
            for (var n = 0; n < 32 && valid(ancestor); n++, ancestor = parent(ancestor)) if (ancestor === panel) { containsHost = true; break; }
            if (containsHost || typeof panel.visible !== 'boolean') continue;
            try { var previous = panel.visible; panel.visible = false; lease.changes.push({ panel: panel, kind: 'visible', previous: previous }); } catch (_) {}
        }
    }
    function backgroundEvidence() {
        var flags = backgroundMode === 'blur' ? 2 : backgroundMode === 'translucent' ? 1 : 0;
        if (sceneLease) flags |= 4;
        if (sceneLease && sceneLease.changes.some(function (change) { return change.kind === 'background'; })) flags |= 8;
        if (sceneLease && sceneLease.changes.some(function (change) { return change.kind === 'visible'; })) flags |= 16;
        if (valid(layoutRoot) && layoutRoot.SetHasClass) flags |= 32;
        if (valid(backdrop) && backdrop.style) flags |= 64;
        if (surfaceProbeUntil) flags |= 128;
        if (probeOpacity) flags |= 256;
        if (backdropPainted) flags |= 512;
        if (backgroundMode !== 'static') flags |= 262144;
        var old = root.__dltSettingsBackgroundEvidence;
        if (!old || old.flags !== flags) root.__dltSettingsBackgroundEvidence = { flags: flags, revision: old ? old.revision + 1 : 1 };
    }
    function paintBackdrop() {
        if (!valid(backdrop) || !backdrop.style) return;
        backdropPainted = false;
        try { backdrop.style.backgroundColor = surfaceProbeUntil ? '#187547ff' : backgroundMode === 'static' ? '#141414ff' : '#000000b3'; backdropPainted = true; } catch (_) {}
    }
    function applyBackground(mode) {
        mode = mode === 'blur' || mode === 'translucent' ? mode : 'static';
        if (mode === backgroundMode || !valid(context) || !context.SetHasClass) return;
        context.SetHasClass('DltBackgroundTranslucent', mode === 'translucent');
        context.SetHasClass('DltBackgroundBlur', mode === 'blur');
        if (valid(layoutRoot) && layoutRoot.SetHasClass) {
            layoutRoot.SetHasClass('DltBackgroundTranslucent', mode === 'translucent');
            layoutRoot.SetHasClass('DltBackgroundBlur', mode === 'blur');
        }
        backgroundMode = mode;
        if (mode === 'static') restoreScene(); else exposeScene();
        paintBackdrop(); backgroundEvidence();
    }
    function backgroundFromTitle(title) {
        var save = /:save:(clean|dirty|saving|error)$/.exec(String(title || ''));
        if(save)unsavedSettings=save[1]!=='clean';
        title=String(title || '').replace(/:save:(clean|dirty|saving|error)$/, '');
        var match = /:background:(static|translucent|blur)$/.exec(String(title || ''));
        if (match) applyBackground(match[1]);
        return String(title || '').replace(/:background:(static|translucent|blur)$/, '');
    }
    applyBackground(root.__dltSettingsBackgroundMode);
    var loopbackHost = root.__dltLoopbackHost === '127.0.0.1' ? '127.0.0.1' : 'localhost';
    var status = context.FindChildTraverse('DltWindowStatus');
    var help = context.FindChildTraverse('DltConnectionHelp');
    var helpTitle = context.FindChildTraverse('DltConnectionHelpTitle');
    var helpText = context.FindChildTraverse('DltConnectionHelpText');
    var lastHelp = '';
    var editor = null, editorSerial = 0, retiredEditorId = '';
    function connectionHelp(kind) {
        if (kind === lastHelp) return;
        lastHelp = kind;
        if (!valid(help) || !help.SetHasClass) return;
        help.SetHasClass('ShowHelp', !!kind);
        if(context.SetHasClass)context.SetHasClass('DltShowNativeHelp',!!kind);
        if (!kind || !valid(helpTitle) || !valid(helpText)) return;
        if (kind === 'unsaved') { helpTitle.text = '修改尚未保存'; helpText.text = '请在页面重试自动保存、处理配置冲突，或明确撤销未保存修改后再关闭。词库需要单独保存。'; return; }
        helpTitle.text = kind === 'unknown' ? '未收到页面状态：若页面可操作，可继续使用' : '设置页连接未完成';
        helpText.text = '若下方空白或不可操作：确认后台已启动后点击“重新加载”，或在程序目录运行 Settings.cmd。游戏与后台应使用同一 Windows 用户；游戏必须管理员运行时，使用管理员兼容启动（原 Windows 用户确认 UAC）。首次信任证书后完整重启 Steam；已有有效证书无需重装。';
    }
    var eventTitle = '', observedTitle = '', unhandled = [], scheduled = null, reconnectConsumed = false, readyFocused = false;
    var navigationPending = false, navigationStartedAt = 0, retiredTitles = [], consumedReloadTitle = '';
    var inputContextOwned = false, lastInputEvidence = -1, focusReturned = null, formFocusSeen = false, focusing = false;
    function acquireInputContext() {
        if (inputContextOwned || closed || !valid(host)) return;
        // A child HTML panel can be focused in the old menu context without becoming
        // the active keyboard target. This window owns its own modal input context.
        try {
            if (host.SetAcceptsInput) host.SetAcceptsInput(true);
            if (host.SetAcceptsFocus) host.SetAcceptsFocus(true);
            if (host.SetTopOfInputContext) { host.SetTopOfInputContext(true); inputContextOwned = true; }
        } catch (_) {}
    }
    function inputEvidence() {
        if (!valid(page)) return;
        var flags = inputContextOwned ? 1 : 0;
        try { if (page.BAcceptsInput && page.BAcceptsInput()) flags |= 2; } catch (_) {}
        try { if (page.BAcceptsFocus && page.BAcceptsFocus()) flags |= 4; } catch (_) {}
        try { if (page.BHasKeyFocus && page.BHasKeyFocus()) flags |= 8; } catch (_) {}
        try { if (page.BHasDescendantKeyFocus && page.BHasDescendantKeyFocus()) flags |= 16; } catch (_) {}
        if (focusReturned === true) flags |= 32;
        if (focusReturned === false) flags |= 64;
        if (formFocusSeen) flags |= 128;
        if (editor) flags |= 256;
        try { if (editor && valid(editor.entry) && editor.entry.BHasKeyFocus && editor.entry.BHasKeyFocus()) flags |= 512; } catch (_) {}
        if (flags === lastInputEvidence) return;
        lastInputEvidence = flags;
        root.__dltSettingsInputEvidence = { flags: flags, revision: (root.__dltSettingsInputEvidence ? root.__dltSettingsInputEvidence.revision : 0) + 1 };
    }
    DLTSettingsFocus = function () {
        if (closed || focusing || !valid(host) || !valid(page) || host.__dltSettingsHost !== true || host.__dltClosing) return;
        focusing = true;
        // Inline editing shares the host context. A click in HTML must be allowed
        // to leave the native field; never pull its focus back on every callback.
        if (editor && editor.phase === 'editing') DLTEditorBlur();
        // Focus before navigation is not enough: the native HTML surface may not yet
        // exist. Hand it input after readiness or an explicit click/form-focus event.
        // This does not change the DOM input/selection and never runs each poll.
        acquireInputContext();
        try { if (page.SetAcceptsInput) page.SetAcceptsInput(true); } catch (_) {}
        try { if (page.SetAcceptsFocus) page.SetAcceptsFocus(true); } catch (_) {}
        try { if (page.SetDisableFocusOnMouseDown) page.SetDisableFocusOnMouseDown(false); } catch (_) {}
        var htmlFocused = false;
        try { htmlFocused = !!page.BHasKeyFocus && page.BHasKeyFocus(); } catch (_) {}
        try { htmlFocused = htmlFocused || !!page.BHasDescendantKeyFocus && page.BHasDescendantKeyFocus(); } catch (_) {}
        var nativeFocused = false;
        try { nativeFocused = editor && valid(editor.entry) && editor.entry.BHasKeyFocus && editor.entry.BHasKeyFocus(); } catch (_) {}
        // A click inside an already focused HTML surface belongs to its DOM control.
        // Repeated native focus commands can reset an open select or its selection.
        if (!htmlFocused || nativeFocused) {
            try { if (page.SetFocus) focusReturned = page.SetFocus(); } catch (_) {}
            try { $.DispatchEvent('SetInputFocus', page); } catch (_) {}
        }
        inputEvidence();
        focusing = false;
    };
    function formFocusPending() {
        for (var i = 0; i < arguments.length; i++) {
            var value = arguments[i];
            if (value && typeof value === 'object' && typeof value.IsValid === 'function' && value !== page) return;
        }
        formFocusSeen = true; DLTSettingsFocus();
    }
    try { $.RegisterEventHandler('HTMLFormFocusPending', page, formFocusPending); } catch (_) {}
    function resumeChat(force) {
        try { var control = root.__dltAdapterControl; return !!control && typeof control.resume === 'function' && control.resume(force); } catch (_) { return false; }
    }
    function currentTitle(title) {
        title = String(title || '');
        var load = /:load:([0-9]+)$/.exec(title);
        if (load && Number(load[1]) !== generation) return '';
        if (!load && navigationPending && retiredTitles.indexOf(title) >= 0) return '';
        return title.replace(/:load:[0-9]+$/, '');
    }
    function stateFromTitle(title) {
        title = currentTitle(title);
        if (!title) return '';
        title = backgroundFromTitle(title);
        if (/^DLTSETTINGS:edit:[a-f0-9]{48}:[0-9]+:[01]:(?:-?[0-9]+,){15}-?[0-9]+$/.test(String(title || ''))) return 'edit';
        var match = /^DLTSETTINGS:(ready|offline|error|close|reconnect|reload|surfaceprobe)(?::[0-9]+)?$/.exec(String(title || ''));
        return match ? match[1] : '';
    }
    function titleEvent() {
        if (closed) return;
        if (!valid(context) || !valid(page)) { release(); return; }
        for (var p = 0; p < arguments.length; p++) {
            var source = arguments[p];
            if (source && typeof source === 'object' && typeof source.IsValid === 'function' && source !== page) return;
        }
        for (var i = 0; i < arguments.length; i++) {
            if (typeof arguments[i] === 'string' && stateFromTitle(arguments[i])) eventTitle = arguments[i];
        }
    }
    function readState() {
        var state = stateFromTitle(eventTitle);
        if (state) { observedTitle = eventTitle; return state; }
        var title = '';
        try { title = page.title; state = stateFromTitle(title); } catch (_) {}
        if (state) { observedTitle = title; return state; }
        try { if (page.GetTitle) { title = page.GetTitle(); state = stateFromTitle(title); } } catch (_) {}
        if (state) { observedTitle = title; return state; }
        try { if (page.GetAttributeString) { title = page.GetAttributeString('title', ''); state = stateFromTitle(title); } } catch (_) {}
        if (state) observedTitle = title;
        return state;
    }
    function editorSignal() {
        var values = [eventTitle];
        try { values.push(page.title); if (page.GetTitle) values.push(page.GetTitle()); } catch (_) {}
        for (var i = 0; i < values.length; i++) {
            var title = currentTitle(values[i]);
            var match = /^DLTSETTINGS:edit:([a-f0-9]{48}):[0-9]+:([01]):((?:-?[0-9]+,){15}-?[0-9]+)$/.exec(title ? backgroundFromTitle(title) : '');
            if (match) {
                var geometry = match[3].split(',').map(Number), safe = true;
                for (var n = 0; n < geometry.length; n++) if (!isFinite(geometry[n]) || Math.abs(geometry[n]) > 65536) safe = false;
                if (safe && geometry[0] > 0 && geometry[1] > 0 && geometry[4] > 0 && geometry[5] > 0 && geometry[10] >= 60 && geometry[10] <= 960 && geometry[8] >= geometry[6] && geometry[9] >= geometry[7]) return { id: match[1], commit: match[2] === '1', geometry: geometry };
            }
        }
        return null;
    }
    function panelPosition(panel) {
        try {
            var point = panel.GetPositionWithinWindow();
            var x = point.x === undefined ? point[0] : point.x, y = point.y === undefined ? point[1] : point.y;
            return typeof x === 'number' && typeof y === 'number' && isFinite(x) && isFinite(y) ? { x: x, y: y } : null;
        } catch (_) { return null; }
    }
    function positionEditor(geometry) {
        if (!editor) return false;
        var origin = panelPosition(context), pageOrigin = panelPosition(page), scaleX = Number(context.actualuiscale_x), scaleY = Number(context.actualuiscale_y);
        var width = Number(page.actuallayoutwidth), height = Number(page.actuallayoutheight);
        if (!origin || !pageOrigin || !isFinite(scaleX) || !isFinite(scaleY) || scaleX <= 0 || scaleY <= 0 || !isFinite(width) || !isFinite(height) || width <= 0 || height <= 0) return false;
        var g = geometry, sx = width / g[0] / scaleX, sy = height / g[1] / scaleY;
        var signature = g.join(',') + ':' + pageOrigin.x + ':' + pageOrigin.y + ':' + origin.x + ':' + origin.y + ':' + width + ':' + height + ':' + scaleX + ':' + scaleY;
        if (signature === editor.positionSignature) return true;
        editor.positionSignature = signature;
        var left = Math.max(0, g[6]), top = Math.max(0, g[7]), right = Math.min(g[0], g[8]), bottom = Math.min(g[1], g[9]);
        var shown = right > left && bottom > top && Math.min(g[2] + g[4], right) > Math.max(g[2], left) && Math.min(g[3] + g[5], bottom) > Math.max(g[3], top);
        editor.panel.style.position = ((pageOrigin.x - origin.x) / scaleX + left * sx) + 'px ' + ((pageOrigin.y - origin.y) / scaleY + top * sy) + 'px 0px';
        editor.panel.style.width = Math.max(0, (right - left) * sx) + 'px'; editor.panel.style.height = Math.max(0, (bottom - top) * sy) + 'px';
        for (var i = 0; i < 2; i++) {
            var entry = i ? editor.multi : editor.single;
            entry.style.position = ((g[2] - left) * sx) + 'px ' + ((g[3] - top) * sy) + 'px 0px';
            entry.style.width = g[4] * sx + 'px'; entry.style.height = g[5] * sy + 'px';
            entry.style.fontSize = g[10] / 10 * sy + 'px';
            entry.style.paddingTop = g[11] / 10 * sy + 'px'; entry.style.paddingRight = g[12] / 10 * sx + 'px';
            entry.style.paddingBottom = g[13] / 10 * sy + 'px'; entry.style.paddingLeft = g[14] / 10 * sx + 'px';
        }
        editor.errorPanel.style.position = Math.max(0, (g[2] - left) * sx) + 'px ' + Math.max(0, Math.min((g[3] + g[5] - top) * sy + 4, (bottom - top) * sy - 72)) + 'px 0px';
        editor.panel.SetHasClass('InlineClipped', !shown);
        return true;
    }
    function focusEditor() {
        if (!editor) return;
        var target = valid(editor.entry) ? editor.entry : context.FindChildTraverse('DltNativeEditCancel');
        try { if (valid(target) && target.SetFocus) target.SetFocus(); } catch (_) {}
    }
    function editorError(message) {
        if (!editor) return;
        editor.phase = 'error'; editor.status.text = message;
        editor.errorPanel.visible = true;
        editor.apply.enabled = true; editor.applyLabel.text = valid(editor.entry) ? '重试回填' : '重试输入';
        if (valid(editor.entry)) editor.entry.enabled = true;
    }
    function sendEditor(action, value) {
        if (!editor) return;
        var control = root.__dltAdapterControl;
        if (!control || typeof control.resume !== 'function') { editorError('游戏通信脚本尚未就绪，请取消后重新打开设置。'); return; }
        var command = { id: editor.id, action: action, request: ++editorSerial };
        if (action === 'apply') command.value = value;
        editor.request = command.request; editor.sentAt = Date.now(); editor.lastResponse = null;
        root.__dltNativeEditorReply = null; root.__dltNativeEditorCommand = command;
        try { control.resume(false); } catch (_) {}
    }
    function hideEditor(keepCommand) {
        if (!editor) return;
        var previous = editor; editor = null; retiredEditorId = previous.id;
        if (!keepCommand && root.__dltNativeEditorCommand && root.__dltNativeEditorCommand.id === previous.id) root.__dltNativeEditorCommand = null;
        if (root.__dltNativeEditorReply && root.__dltNativeEditorReply.id === previous.id) root.__dltNativeEditorReply = null;
        try { if (valid(previous.panel) && previous.ownsContext && previous.panel.SetTopOfInputContext) previous.panel.SetTopOfInputContext(false); } catch (_) {}
        try { previous.single.text = ''; previous.multi.text = ''; previous.panel.RemoveClass('ShowEditor'); } catch (_) {}
    }
    function startEditor(signal) {
        var id = signal.id;
        if (editor && editor.id === id) return;
        hideEditor();
        var panel = context.FindChildTraverse('DltNativeEditor'), single = context.FindChildTraverse('DltNativeEditSingle'), multi = context.FindChildTraverse('DltNativeEditMulti');
        var errorPanel = context.FindChildTraverse('DltNativeEditError'), editorStatus = context.FindChildTraverse('DltNativeEditStatus');
        var apply = context.FindChildTraverse('DltNativeEditApply'), applyLabel = context.FindChildTraverse('DltNativeEditApplyLabel');
        if (!valid(panel) || !valid(single) || !valid(multi) || !valid(errorPanel) || !valid(editorStatus) || !valid(apply) || !valid(applyLabel)) { status.text = '缺少原生编辑控件，请成套更新 VPK 和后台'; return; }
        editor = { id: id, phase: 'loading', panel: panel, single: single, multi: multi, entry: null,
            geometry: signal.geometry, status: editorStatus, errorPanel: errorPanel, apply: apply, applyLabel: applyLabel };
        single.text = ''; multi.text = ''; single.visible = false; multi.visible = false;
        editor.status.text = ''; editor.errorPanel.visible = false; editor.apply.enabled = false;
        panel.AddClass('ShowEditor');
        positionEditor(signal.geometry); sendEditor('claim');
    }
    DLTEditorCancel = function () {
        if (!editor) return true;
        sendEditor('cancel'); hideEditor(true); DLTSettingsFocus(); return true;
    };
    DLTEditorApply = function () {
        if (!editor || editor.phase === 'returning' || editor.phase === 'loading') return true;
        if (!valid(editor.entry)) { editor.phase = 'loading'; editor.apply.enabled = false; sendEditor('claim'); return true; }
        var text = String(editor.entry.text || '');
        editor.phase = 'returning'; editor.status.text = ''; editor.errorPanel.visible = false; editor.apply.enabled = false; editor.entry.enabled = false;
        sendEditor('apply', text); return true;
    };
    function queueEditorCommit(force) {
        var current = editor;
        if (!current || current.phase !== 'editing') return;
        if (force) current.forceCommit = true;
        if (current.commitScheduled) return;
        current.commitScheduled = true;
        // Allow the IME to finish committing and an in-field click to regain focus.
        $.Schedule(0.08, function () {
            if (editor !== current || current.phase !== 'editing') return;
            current.commitScheduled = false;
            try { if (!current.forceCommit && current.entry.BHasKeyFocus && current.entry.BHasKeyFocus()) return; } catch (_) {}
            DLTEditorApply();
        });
    }
    DLTEditorBlur = function () { queueEditorCommit(false); };
    function editorTick(state) {
        var signal = state === 'edit' ? editorSignal() : null, id = signal ? signal.id : '';
        if (!id) { if (editor) hideEditor(); retiredEditorId = ''; return; }
        if (!editor && id !== retiredEditorId) startEditor(signal);
        if (!editor) return;
        editor.geometry = signal.geometry;
        var positioned = positionEditor(signal.geometry);
        var result = root.__dltNativeEditorReply;
        if (result && result !== editor.lastResponse) {
            editor.lastResponse = result;
            try {
                if (result.id === editor.id && result.request === editor.request) {
                    if (result.state === 'error') editorError(String(result.message || '编辑未完成，请重试。'));
                    else if (result.state === 'applied' || result.state === 'cancelled') { hideEditor(); return; }
                    else if (result.state === 'editing' && editor.phase === 'loading') {
                        if (typeof result.value !== 'string' || typeof result.maxLength !== 'number' || result.maxLength !== Math.floor(result.maxLength) || result.maxLength < 1 || result.maxLength > 4096 || result.value.length > result.maxLength) { editorError('编辑内容无效，原页面内容未改变。'); return; }
                        editor.single.visible = !result.multiline; editor.multi.visible = !!result.multiline;
                        editor.entry = result.multiline ? editor.multi : editor.single;
                        if (editor.entry.SetMaxChars) editor.entry.SetMaxChars(result.maxLength);
                        editor.entry.text = result.value; editor.entry.enabled = true;
                        editor.phase = 'editing'; editor.status.text = ''; editor.apply.enabled = true;
                        if (!positioned) { editorError('无法定位文字框，请重新加载设置或暂用浏览器编辑。'); return; }
                        if (!signal.commit) focusEditor();
                    }
                }
            } catch (_) { editorError('编辑回传格式无效，原页面内容未改变。'); }
        }
        if (editor && editor.phase === 'editing' && signal.commit) queueEditorCommit(true);
        if (editor && (editor.phase === 'loading' || editor.phase === 'returning') && Date.now() - editor.sentAt > 8000) editorError('后台响应超时，编辑内容仍保留；可以重试或取消。');
    }
    var titleEvents = ['HTMLChangedTitle', 'HTMLTitle'];
    for (var e = 0; e < titleEvents.length; e++) {
        try { $.RegisterEventHandler(titleEvents[e], page, titleEvent); } catch (_) {}
        // Only register global fallbacks when they can also be removed on close.
        if ($.RegisterForUnhandledEvent && $.UnregisterForUnhandledEvent) {
            try { unhandled.push({ name: titleEvents[e], handle: $.RegisterForUnhandledEvent(titleEvents[e], titleEvent) }); } catch (_) {}
        }
    }
    function valid(panel) { try { return !!panel && panel.IsValid(); } catch (_) { return false; } }
    function parent(panel) { try { return valid(panel) ? panel.GetParent() : null; } catch (_) { return null; } }
    function cancelPoll() {
        if (scheduled !== null && $.CancelScheduled) { try { $.CancelScheduled(scheduled); } catch (_) {} }
        scheduled = null;
    }
    function release() {
        if (closed) return;
        hideEditor();
        if (inputContextOwned) { try { if (valid(host) && host.SetTopOfInputContext) host.SetTopOfInputContext(false); } catch (_) {} inputContextOwned = false; }
        surfaceProbeUntil = 0; updateProbe(); applyBackground('static'); restoreScene();
        navigationPending = false; retiredTitles = [];
        closed = true; ++generation; cancelPoll();
        try { if (root.__dltActiveSettingsWindow === context) root.__dltActiveSettingsWindow = null; if (root.__dltActiveSettingsHost === host) root.__dltActiveSettingsHost = null; } catch (_) {}
        for (var i = 0; i < unhandled.length; i++) { try { $.UnregisterForUnhandledEvent(unhandled[i].name, unhandled[i].handle); } catch (_) {} }
        unhandled = [];
        if ($.UnregisterEventHandler) {
            if (valid(page)) for (var i = 0; i < titleEvents.length; i++) { try { $.UnregisterEventHandler(titleEvents[i], page, titleEvent); } catch (_) {} }
            try { if (valid(page)) $.UnregisterEventHandler('HTMLFormFocusPending', page, formFocusPending); } catch (_) {}
            try { if (valid(context)) $.UnregisterEventHandler('PanelDeleted', context, panelDeleted); } catch (_) {}
        }
    }
    function panelDeleted(panel) { if (panel === context || !valid(context)) release(); }
    try { $.RegisterEventHandler('PanelDeleted', context, panelDeleted); } catch (_) {}
    function escapeVisible() {
        var ancestor = parent(context), visits = 0;
        while (valid(ancestor) && visits++ < 16) {
            if (ancestor.BHasClass('ShowEscapeMenu')) return true;
            ancestor = parent(ancestor);
        }
        return false;
    }
    function ownerHidden() {
        var owner = parent(host);
        if (!valid(owner) || owner.visible === false) return true;
        var visible = escapeVisible();
        if (visible) { observedEscape = true; hiddenTicks = 0; return false; }
        // Computed BIsVisible can change while the HTML child takes focus. Require a
        // sustained native menu-state change instead of treating it as a close command.
        if (observedEscape) hiddenTicks++;
        return hiddenTicks >= 2;
    }
    DLTSettingsClose = function (reason) {
        if (closed) return;
        if (editor && (reason === 'escape' || reason === 'page-escape')) { DLTEditorCancel(); return; }
        if(unsavedSettings && reason !== 'page-escape' && reason !== 'menu-hidden' && reason !== 'page-destroyed'){status.text='修改尚未保存，请在页面重试或撤销修改后关闭';connectionHelp('unsaved');return;}
        var canDelete = valid(host) && host.__dltSettingsHost === true && valid(parent(host));
        release();
        try { var control = root.__dltAdapterControl; if (control && control.afterSettingsClose) control.afterSettingsClose(); } catch (_) {}
        if (valid(context)) context.__dltClosing = true;
        if (valid(host)) host.__dltClosing = true;
        try { $.Msg('[DLT] settings window closed: ' + (reason || 'close-button')); } catch (_) {}
        // Deleting the owned host releases its HTML document. Starting a navigation while
        // deleting it can race native browser/graphics teardown during a scene transition.
        if (canDelete) {
            try { if (reason !== 'menu-hidden' && valid(page)) $.DispatchEvent('DropInputFocus', page); } catch (_) {}
            host.visible = false; host.DeleteAsync(0);
        }
    };
    DLTSettingsRetry = function () {
        if (closed || !valid(page)) return false;
        // SetURL is asynchronous. The old document can keep publishing its old title
        // until CEF attaches the new frame; never navigate again during that interval.
        if (navigationPending && Date.now() - navigationStartedAt < 10000) return false;
        if (generation > 0 && (editor || unsavedSettings)) {
            status.text = '修改尚未保存，请先重试保存或撤销修改，再重新加载';
            connectionHelp('unsaved'); return false;
        }
        if (surfaceProbeUntil) { surfaceProbeUntil = 0; updateProbe(); paintBackdrop(); backgroundEvidence(); }
        retiredTitles = [eventTitle, observedTitle];
        try { retiredTitles.push(String(page.title || '')); } catch (_) {}
        try { if (page.GetTitle) retiredTitles.push(String(page.GetTitle() || '')); } catch (_) {}
        try { if (page.GetAttributeString) retiredTitles.push(String(page.GetAttributeString('title', '') || '')); } catch (_) {}
        hideEditor(); retiredEditorId = '';
        cancelPoll();
        // Settings navigation must not force-navigate the healthy translation bridge.
        // Explicit "restore connection" remains the separate force=true action.
        resumeChat(false);
        var request = ++generation, ticks = 0, receivedState = false;
        navigationPending = true; navigationStartedAt = Date.now();
        if (request > 1) loopbackHost = loopbackHost === 'localhost' ? '127.0.0.1' : 'localhost';
        eventTitle = ''; observedTitle = '';
        readyFocused = false;
        connectionHelp('');
        observedEscape = observedEscape || escapeVisible();
        status.text = '正在连接本地设置…';
        try {
            page.SetURL('https://' + loopbackHost + ':17866/in-game/__PAIRING__?open=' + Date.now() + '&windowProtocol=2&windowLoad=' + request + '&surfaceProbe=5');
        } catch (_) { navigationPending = false; status.text = '页面加载失败，请确认翻译后台已启动'; connectionHelp('error'); return false; }
        try { if (page.SetFocus) page.SetFocus(); } catch (_) {}
        function poll() {
            if (closed || request !== generation) return;
            scheduled = null;
            if (!valid(context)) { release(); return; }
            if (!valid(page) || !valid(status)) { DLTSettingsClose('page-destroyed'); return; }
            if (ownerHidden()) { DLTSettingsClose('menu-hidden'); return; }
            var pageState = readState();
            if (surfaceProbeUntil && (Date.now() >= surfaceProbeUntil || pageState && pageState !== 'surfaceprobe')) { surfaceProbeUntil = 0; updateProbe(); paintBackdrop(); backgroundEvidence(); }
            if (pageState === 'surfaceprobe') {
                var probeMatch = /^DLTSETTINGS:surfaceprobe:([0-9]+)/.exec(String(observedTitle || ''));
                var probeKey = generation + ':' + (probeMatch ? probeMatch[1] : '');
                if (probeMatch && probeKey !== consumedProbeTitle) { consumedProbeTitle = probeKey; surfaceProbeUntil = Date.now() + 6000; paintBackdrop(); }
                if (surfaceProbeUntil) updateProbe();
                pageState = 'ready';
            }
            editorTick(pageState);
            if (pageState === 'edit') inputEvidence();
            if (pageState === 'reconnect') {
                if (!reconnectConsumed) { reconnectConsumed = true; status.text = resumeChat(true) ? '已请求恢复游戏连接，请等待连接状态更新' : '游戏脚本无法恢复，请同步更新 VPK 后重启游戏'; }
            } else reconnectConsumed = false;
            if (pageState === 'reload') {
                if (observedTitle !== consumedReloadTitle) {
                    // Consume before navigating: a stale property/GetTitle value is
                    // an already handled command, not a fresh request on every poll.
                    consumedReloadTitle = observedTitle;
                    if (DLTSettingsRetry()) return;
                }
                pageState = '';
            }
            if (pageState) navigationPending = false;
            if (pageState === 'close') { DLTSettingsClose('page-escape'); return; }
            if (pageState) receivedState = true;
            if (pageState === 'ready') {
                if (!readyFocused) { backgroundEvidence(); readyFocused = true; DLTSettingsFocus(); }
                inputEvidence();
                resumeChat(false);
                var progress = root.__dltTranslationProgress || {}, note = '';
                if (progress.thinking) note += ' · 思考 ' + progress.thinking;
                if (progress.generating) note += ' · 生成 ' + progress.generating;
                if (progress.waiting) note += ' · 等待 ' + progress.waiting;
                var message = '设置页已连接' + note + ' · 更改自动保存 · Esc 关闭';
                if (status.text !== message) status.text = message;
                connectionHelp('');
            }
            if (pageState === 'offline') { status.text = '页面已加载，后台连接中断；可点击重新加载'; connectionHelp('offline'); }
            if (pageState === 'error') { status.text = '页面初始化失败，请查看页面提示或重新加载'; connectionHelp('error'); }
            if (!receivedState && ++ticks >= 40) { status.text = '尚未收到页面状态；若下方可操作，可继续使用或重新加载'; connectionHelp('unknown'); }
            scheduled = $.Schedule(editor ? 0.05 : 0.2, poll);
        }
        scheduled = $.Schedule(0.2, poll);
        return true;
    };
    scheduled = $.Schedule(0.05, DLTSettingsRetry);
}());
