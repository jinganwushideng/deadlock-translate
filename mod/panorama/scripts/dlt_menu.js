// Create an in-game settings window only on click; never submit or reset chat jobs.
var DLTMenuOpenSettings = function () {};
(function () {
    'use strict';
    var context = $.GetContextPanel();
    // Match the existing native/QOL row pattern and keep the menu's own positioning.
    // Reserve only the lower section's height; do not turn the whole menu into a new layout.
    function reserveMenuSpace() {
        if (!context.IsValid()) return;
        var options = context.GetParent();
        while (options && options.id !== 'SubOptions') options = options.GetParent();
        if (!options) return;
        var menu = options.GetParent();
        if (!menu || menu.id !== 'Menu') return;
        var height = 0;
        for (var r = 0; r < options.GetChildCount(); r++) {
            var row = options.GetChild(r);
            if (row.visible === false) continue;
            height += row.id === 'quit' ? 60 : row.BHasClass('FeedbackRow') ? 60 : 32;
        }
        var scale = Number(options.actualuiscale_y);
        var measured = Number(options.actuallayoutheight);
        if (scale > 0 && measured > 0 && measured / scale < 500) height = measured / scale;
        var heroBottom = Math.max(280, 132 + height + 20);
        var hero = menu.FindChildTraverse('changehero');
        if (hero) hero.style.marginBottom = heroBottom + 'px';
        var primary = ['guides', 'watchgame', 'newgame'];
        for (var i = 0; i < primary.length; i++) {
            var button = menu.FindChildTraverse(primary[i]);
            if (!button) continue;
            var labels = button.FindChildrenWithClassTraverse('menuButtonLabel');
            for (var j = 0; j < labels.length; j++) labels[j].style.marginBottom = (heroBottom + (i + 1) * 70) + 'px';
        }
    }
    $.Schedule(0.05, reserveMenuSpace);
    $.Schedule(0.3, reserveMenuSpace);
    function label(value) {
        var status = context.FindChildTraverse('DltMenuStatus');
        if (status) status.text = value;
    }
    DLTMenuOpenSettings = function () {
        var owner = context, visits = 0;
        while (owner && visits++ < 16) {
            if (owner.FindChildTraverse('EscapeBackground') && owner.FindChildTraverse('Menu')) break;
            owner = owner.GetParent();
        }
        if (!owner || visits > 16) { label('未找到 Esc 菜单窗口'); return; }
        var previous = owner.__dltSettingsWindow;
        if (previous && previous.IsValid() && !previous.__dltClosing) return;
        try {
            var window = $.CreatePanel('Panel', owner, 'DltSettingsWindowHost');
            owner.__dltSettingsWindow = window;
            window.style.width = '100%'; window.style.height = '100%';
            var loaded = window.BLoadLayout('file://{resources}/layout/dlt_settings_window.xml', false, false);
            if (loaded === false) throw new Error('Settings layout not loaded');
            label('');
        } catch (_) {
            if (owner.__dltSettingsWindow && owner.__dltSettingsWindow.IsValid()) owner.__dltSettingsWindow.DeleteAsync(0);
            label('游戏内设置加载失败，请同步更新 Mod 和后台');
        }
    };
}());
