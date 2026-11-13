(function (root) {
    'use strict';
    $.Schedule(0, function () { initialize(root); });

function initialize(root) {
    'use strict';
    if (!root || !root.IsValid()) return;
    var button = root.FindChildTraverse('DltBuildTranslate');
    var shop = !button && root.FindChildTraverse('DltShopBridge');
    var toggle = button || (shop && root.FindChildTraverse('DltShopTranslate'));
    var category = !button && !shop && root.FindChildTraverse('BuildCategoryName');
    var prefix = category ? 'DltCategory' : 'DltBuild';
    var bridge = shop || (button && root.FindChildTraverse('DltBuildBridge'));
    var selected = shop && root.FindChildTraverse('SelectedBuildOuter');
    var originals = shop ? [] : [root.FindChildTraverse(button ? 'DltBuildTitle' : category ? 'BuildCategoryName' : 'ModName'),
        root.FindChildTraverse(prefix + 'Note')];
    var outputs = [root.FindChildTraverse(prefix + 'TitleTranslation'), root.FindChildTraverse(prefix + 'NoteTranslation')];
    if ((button && !bridge) || originals.some(function (label) { return !label; }) ||
        (!shop && !category && outputs.some(function (label) { return !label; }))) return;

    var scene = root;
    while (scene.GetParent()) scene = scene.GetParent();
    var state = scene.__dltBuildText;
    if (!state) {
        state = { owner: null, enabled: false, cache: Object.create(null), order: [],
            queue: [], pending: null, retry: Object.create(null), retryOrder: [], seq: 0, revision: '' };
        scene.__dltBuildText = state;
    }
    var eventTitle = '';
    if (bridge) ['HTMLChangedTitle', 'HTMLTitle'].forEach(function (name) {
        try { $.RegisterEventHandler(name, bridge, function () {
            for (var i = 0; i < arguments.length; i++) {
                if (typeof arguments[i] === 'string' && arguments[i].indexOf('DLTBUILD:') === 0) eventTitle = arguments[i];
            }
        }); } catch (_) {}
    });
    var shown = '', displayed = null;

    function resetRevision(revision) {
        state.revision = revision;
        state.cache = Object.create(null);
        state.order = [];
        state.queue = [];
        state.pending = null;
        state.retry = Object.create(null);
        state.retryOrder = [];
    }
    function syncRevision() {
        var config = scene.__dltBuildTextConfig;
        if (config && config.revision !== state.revision) resetRevision(config.revision);
    }
    function fail(job, now) {
        if (!state.retry[job.key]) {
            if (state.retryOrder.length >= 256) delete state.retry[state.retryOrder.shift()];
            state.retryOrder.push(job.key);
        }
        var previous = state.retry[job.key];
        state.retry[job.key] = { count: previous ? previous.count + 1 : 1, after: now + 4000 };
        state.pending = null;
    }

    function pump() {
        var now = Date.now();
        var job = state.pending;
        if (job) {
            try {
                var reply = eventTitle || String(bridge.title || '');
                if (reply.indexOf('DLTBUILD:') !== 0 && bridge.GetTitle) reply = String(bridge.GetTitle() || '');
                if (reply.indexOf('DLTBUILD:') === 0) {
                    var result = JSON.parse(reply.slice(9));
                    if (result.id === job.id && result.error) {
                        if (result.error === 'settings-changed' && typeof result.revision === 'string') {
                            if (scene.__dltBuildTextConfig) scene.__dltBuildTextConfig.revision = result.revision;
                            resetRevision(result.revision);
                        } else fail(job, now);
                    } else if (result.id === job.id && (!state.revision || result.revision === state.revision) &&
                        Array.isArray(result.translations) && result.translations.length === 2 &&
                        result.translations.every(function (value) { return typeof value === 'string'; })) {
                        if (state.order.length >= 256) delete state.cache[state.order.shift()];
                        state.order.push(job.key);
                        state.cache[job.key] = result.translations;
                        var title = JSON.parse(job.key)[0];
                        if (title && result.translations[0]) {
                            var titleKey = JSON.stringify([title, '']);
                            if (titleKey !== job.key && !state.cache[titleKey]) {
                                if (state.order.length >= 256) delete state.cache[state.order.shift()];
                                state.order.push(titleKey);
                                state.cache[titleKey] = [result.translations[0], ''];
                            }
                        }
                        state.pending = null;
                    }
                }
            } catch (_) {}
            if (state.pending && now >= job.deadline) fail(job, now);
        }
        if (!state.enabled || state.pending || !state.queue.length) return;
        job = state.queue.shift();
        var fields = JSON.parse(job.key);
        job.id = ++state.seq;
        job.deadline = now + 18000;
        state.pending = job;
        eventTitle = '';
        try {
            bridge.SetURL('https://localhost:17866/build-text/__PAIRING__#' +
                encodeURIComponent(JSON.stringify({ id: job.id, title: fields[0], note: fields[1], revision: state.revision })));
        } catch (_) { fail(job, now); }
    }
    function request(key, priority) {
        var failure = state.retry[key];
        if (state.cache[key] || (state.pending && state.pending.key === key) ||
            state.queue.some(function (job) { return job.key === key; }) ||
            (failure && (failure.count >= 2 || failure.after > Date.now()))) return;
        if (state.queue.length >= 48) state.queue.pop();
        if (priority) state.queue.unshift({ key: key });
        else state.queue.push({ key: key });
    }
    function isVisible() {
        for (var panel = root; panel; panel = panel.GetParent()) {
            if (panel.visible === false || panel.style.visibility === 'collapse' ||
                panel.style.visibility === 'hidden') return false;
        }
        return true;
    }

    var applied = [null, null];
    function originalText(index) {
        var text = String(originals[index].text || '');
        var last = applied[index];
        return last && text === last.translation ? last.source : text;
    }
    function read(index, max) {
        var text = originalText(index).trim();
        return text.length <= max && !/^[#{]/.test(text) &&
            /[A-Za-z]{2}/.test(text) && !/[\u3400-\u9fff]/.test(text) ? text : '';
    }
    function render(values) {
        for (var i = 0; i < originals.length; i++) {
            if (category || shop) {
                var source = originalText(i);
                var translation = values[i];
                if (translation || applied[i]) originals[i].text = translation || source;
                applied[i] = translation ? { source: source, translation: translation } : null;
                continue;
            }
            outputs[i].text = values[i] || '';
            outputs[i].visible = Boolean(values[i]);
        }
    }
    var empty = [];
    function updateView() {
        if (shop && selected) {
            var label = selected.FindChildTraverse('DltShopTitle');
            if (label !== originals[0]) {
                if (originals[0] && originals[0].IsValid()) render(empty);
                originals = label ? [label] : [];
                shown = '';
                displayed = null;
            }
        }
        var config = scene.__dltBuildTextConfig;
        var active = state.enabled && (!config || config.enabled) && isVisible() && originals.length > 0;
        var key = '';
        if (active) {
            var title = read(0, 240), note = shop ? '' : read(1, 2000);
            if (!button && !category && !shop && (!note || title === note)) title = '';
            if (title || note) key = JSON.stringify([title, note]);
        }
        var values = (key && state.cache[key]) || empty;
        if (shown !== key || displayed !== values) {
            shown = key;
            displayed = values;
            render(values);
        }
        if (key && values === empty) request(key, !category);
        if (toggle) {
            var label = !state.enabled ? '翻译配装' : config && !config.enabled ? '后台翻译已停用 · 关闭' :
                key && values === empty ? (state.retry[key] ? '翻译暂不可用 · 关闭' : '翻译中 · 关闭') : '关闭翻译';
            if (toggle.text !== label) toggle.text = label;
        }
    }
    if (toggle) toggle.SetPanelEvent('onactivate', function () {
        var turningOff = state.enabled;
        syncRevision();
        var cancel = state.pending && state.ownerCancel;
        state.pending = null;
        if (cancel) cancel();
        if (state.owner !== root) {
            assignOwner();
        }
        if (!turningOff && !state.revision) resetRevision('');
        state.retry = Object.create(null);
        state.retryOrder = [];
        state.enabled = !turningOff;
        state.queue = [];
        updateView();
        pump();
    });

    function assignOwner() {
        state.owner = root;
        state.ownerVisible = isVisible;
        state.ownerCancel = function () {
            try { if (bridge.IsValid()) bridge.SetURL('about:blank'); } catch (_) {}
        };
    }
    function tick() {
        if (!root.IsValid()) {
            if (state.owner === root) {
                state.owner = null;
                state.ownerCancel = null;
                state.pending = null;
                state.queue = [];
            }
            return;
        }
        var cancel = state.pending && state.ownerCancel;
        syncRevision();
        if (cancel && !state.pending) cancel();
        if (bridge && isVisible() &&
            (!state.owner || !state.owner.IsValid() || !state.ownerVisible())) {
            if (state.pending) {
                state.queue.unshift({ key: state.pending.key });
                state.ownerCancel();
            }
            state.pending = null;
            assignOwner();
        }
        updateView();
        if (state.owner === root && isVisible()) pump();
        $.Schedule(0.1, tick);
    }
    $.Schedule(0, tick);
}
})($.GetContextPanel());
