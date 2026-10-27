// Create an in-game settings window only on click; never submit or reset chat jobs.
var DLTMenuOpenSettings = function () {};
(function () {
    'use strict';
    var context = $.GetContextPanel();
    function valid(panel) { try { return !!panel && panel.IsValid(); } catch (_) { return false; } }
    // Positioning belongs to the native menu / QOL. This adapter only opens its own window.
    // Never assign margins to their buttons or labels: parent and child margins add together.
    function label(value) {
        if (!valid(context)) return;
        var status = context.FindChildTraverse('DltMenuStatus');
        if (valid(status)) status.text = value;
    }
    DLTMenuOpenSettings = function () {
        if (!valid(context)) return;
        var owner = context, visits = 0;
        while (valid(owner) && visits++ < 16) {
            if (owner.FindChildTraverse('EscapeBackground') && owner.FindChildTraverse('Menu')) break;
            owner = owner.GetParent();
        }
        if (!valid(owner) || visits > 16) { label('未找到 Esc 菜单窗口'); return; }
        try {
            var root = owner;
            for (var p = 0; p < 32 && root.GetParent && root.GetParent(); p++) root = root.GetParent();
            if (root.__dltAdapterControl) root.__dltAdapterControl.resume(true);
        } catch (_) {}
        var previous = owner.__dltSettingsWindow;
        if (previous && previous.IsValid() && !previous.__dltClosing) return;
        try {
            var window = $.CreatePanel('Panel', owner, 'DltSettingsWindowHost');
            window.__dltSettingsHost = true; root.__dltActiveSettingsHost = window;
            owner.__dltSettingsWindow = window;
            window.style.width = '100%'; window.style.height = '100%';
            var loaded = window.BLoadLayout('file://{resources}/layout/dlt_settings_window.xml', false, false);
            if (loaded === false) throw new Error('Settings layout not loaded');
            label('');
        } catch (_) {
            if (window && valid(window) && window.__dltSettingsHost === true) window.DeleteAsync(0);
            if (root && root.__dltActiveSettingsHost === window) root.__dltActiveSettingsHost = null;
            label('游戏内设置加载失败，请同步更新 Mod 和后台');
        }
    };
}());
