/* A single static material layer; the game host owns scene transparency and world blur. */
(function () {
    'use strict';
    var modes = ['static', 'translucent', 'blur'], requested = 'static', context = { nativeAvailable: false, previewScene: false };
    function apply(mode, options) {
        requested = modes.indexOf(mode) >= 0 ? mode : 'static';
        if (options) context = Object.assign({}, context, options);
        var effective = context.nativeAvailable || context.previewScene ? requested : 'static';
        if (document.documentElement.getAttribute('data-settings-background') !== effective) document.documentElement.setAttribute('data-settings-background', effective);
        if (document.documentElement.getAttribute('data-settings-background-requested') !== requested) document.documentElement.setAttribute('data-settings-background-requested', requested);
        if (document.documentElement.classList.contains('dlt-background-demo') !== !!context.previewScene) document.documentElement.classList.toggle('dlt-background-demo', !!context.previewScene);
        var select = document.getElementById('settingsBackgroundMode');
        if (select && select.value !== requested) select.value = requested;
        var status = document.getElementById('settingsBackgroundStatus');
        if (status) {
            var message = effective !== requested ? '已选择该模式；当前页面没有可用的游戏透景通道，暂用静态背景。成套更新后台和 VPK 后，在游戏中打开设置生效。' : context.previewScene ? '透景示意：底层为本页的演示图层，不是实际游戏；不代表游戏帧率。' : requested === 'blur' ? '透出实时游戏画面并使用游戏原生模糊，背景压暗以便阅读；仅设置打开时生效。实际开销取决于显卡和分辨率。' : requested === 'translucent' ? '透出游戏画面，保留暗色遮罩和静态纹理；不额外启用模糊。游戏自身已有的菜单效果仍由游戏决定。' : '静态纹理背景，不启用透景或实时模糊。';
            if (status.textContent !== message) status.textContent = message;
        }
        return { requested: requested, effective: effective };
    }
    window.DltSettingsBackground = { apply: apply, mode: function () { return requested; } };
    var select = document.getElementById('settingsBackgroundMode');
    if (select) select.addEventListener('change', function () { apply(select.value); });
    apply('static');
}());
