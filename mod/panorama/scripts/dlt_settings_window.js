var DLTSettingsClose = function () {};
var DLTSettingsRetry = function () {};
// A click on the window/frame is handled here, never as a backdrop-close gesture.
var DLTSettingsKeepOpen = function () { return true; };
(function () {
    'use strict';
    var context = $.GetContextPanel(), generation = 0, closed = false, observedEscape = false, hiddenTicks = 0;
    var page = context.FindChildTraverse('DltSettingsPage');
    var status = context.FindChildTraverse('DltWindowStatus');
    var eventTitle = '', unhandled = [];
    function stateFromTitle(title) {
        var match = /^DLTSETTINGS:(ready|offline|error|close)(?::[0-9]+)?$/.exec(String(title || ''));
        return match ? match[1] : '';
    }
    function titleEvent() {
        if (closed) return;
        for (var i = 0; i < arguments.length; i++) {
            if (typeof arguments[i] === 'string' && stateFromTitle(arguments[i])) eventTitle = arguments[i];
        }
    }
    function readState() {
        var state = stateFromTitle(eventTitle);
        if (state) return state;
        try { state = stateFromTitle(page.title); } catch (_) {}
        if (state) return state;
        try { if (page.GetTitle) state = stateFromTitle(page.GetTitle()); } catch (_) {}
        if (state) return state;
        try { if (page.GetAttributeString) state = stateFromTitle(page.GetAttributeString('title', '')); } catch (_) {}
        return state;
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
    var host = context, ancestor = context, hostVisits = 0;
    while (valid(ancestor) && hostVisits++ < 8) {
        if (ancestor.id === 'DltSettingsWindowHost') { host = ancestor; break; }
        ancestor = ancestor.GetParent();
    }
    function escapeVisible() {
        var parent = context.GetParent(), visits = 0;
        while (valid(parent) && visits++ < 16) {
            if (parent.BHasClass('ShowEscapeMenu')) return true;
            parent = parent.GetParent();
        }
        return false;
    }
    function ownerHidden() {
        var owner = host.GetParent();
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
        closed = true; ++generation; context.__dltClosing = true; host.__dltClosing = true;
        for (var i = 0; i < unhandled.length; i++) {
            try { $.UnregisterForUnhandledEvent(unhandled[i].name, unhandled[i].handle); } catch (_) {}
        }
        unhandled = [];
        try { $.Msg('[DLT] settings window closed: ' + (reason || 'close-button')); } catch (_) {}
        // Unload the web document before deleting the panel: no settings-page timers remain active.
        try { if (valid(page)) { $.DispatchEvent('DropInputFocus', page); page.SetURL('about:blank'); } } catch (_) {}
        if (valid(host)) { host.visible = false; host.DeleteAsync(0); }
    };
    DLTSettingsRetry = function () {
        if (closed || !valid(page)) return;
        var request = ++generation, ticks = 0, receivedState = false;
        eventTitle = '';
        observedEscape = observedEscape || escapeVisible();
        status.text = '正在连接本地设置…';
        try {
            page.SetURL('http://localhost:17865/in-game/__PAIRING__?open=' + Date.now());
        } catch (_) { status.text = '页面加载失败，请确认翻译后台已启动'; return; }
        try { if (page.SetFocus) page.SetFocus(); } catch (_) {}
        function poll() {
            if (closed || request !== generation || !valid(context)) return;
            if (ownerHidden()) { DLTSettingsClose('menu-hidden'); return; }
            var pageState = readState();
            if (pageState === 'close') { DLTSettingsClose('page-escape'); return; }
            if (pageState) receivedState = true;
            if (pageState === 'ready') status.text = '设置页已连接 · 保存后生效 · Esc 关闭';
            if (pageState === 'offline') status.text = '页面已加载，后台连接中断；可点击重新加载';
            if (pageState === 'error') status.text = '页面初始化失败，请查看页面提示或重新加载';
            if (!receivedState && ++ticks >= 40) status.text = '尚未收到页面状态；若下方可操作，可继续使用或重新加载';
            $.Schedule(0.2, poll);
        }
        $.Schedule(0.2, poll);
    };
    $.Schedule(0.05, DLTSettingsRetry);
}());
