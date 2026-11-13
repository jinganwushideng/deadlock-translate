function DLTBuildTextAction(showOriginal) {
    for (var panel = $.GetContextPanel(); panel; panel = panel.GetParent()) {
        if (panel.__dltBuildTextAction) {
            panel.__dltBuildTextAction(showOriginal);
            return;
        }
    }
}

(function (root) {
    'use strict';
    $.Schedule(0, initialize);
    function initialize() {
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
        if ((toggle && !bridge) || originals.some(function (label) { return !label; }) ||
            (!shop && !category && outputs.some(function (label) { return !label; }))) return;

        var scene = root;
        while (scene.GetParent()) scene = scene.GetParent();
        var state = scene.__dltBuildText;
        if (!state) {
            state = { owner: null, controls: [], readers: [], clicks: 0, error: false, enabled: false, autoEnabled: null,
                revision: '', seq: 0, job: null, signature: null };
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
        var applied = [null, null];
        function isVisible() {
            for (var panel = root; panel; panel = panel.GetParent()) {
                if (panel.visible === false || panel.style.visibility === 'collapse' ||
                    panel.style.visibility === 'hidden') return false;
            }
            return true;
        }
        function read(index) {
            var label = originals[index];
            if (!label || !label.IsValid()) return '';
            var text = String(label.text || '');
            var last = applied[index];
            if (last && last.panel === label && last.translation === text) text = last.source;
            text = text.trim();
            return text.length <= 6000 && !/^[#{]/.test(text) && /[\p{L}]/u.test(text) ? text : '';
        }
        function render(texts, values) {
            for (var i = 0; i < originals.length; i++) {
                var label = originals[i];
                if (!label || !label.IsValid()) continue;
                var translation = values && values[texts[i]] || '';
                if (category || shop) {
                    var last = applied[i];
                    if (translation) {
                        var originalHtml = last && last.panel === label ? last.html : label.html;
                        label.html = false;
                        if (label.text !== translation) label.text = translation;
                        applied[i] = { panel: label, source: texts[i], translation: translation, html: originalHtml };
                    } else if (last && last.panel === label) {
                        if (typeof last.html === 'boolean') label.html = last.html;
                        if (label.text === last.translation) label.text = last.source;
                        applied[i] = null;
                    }
                } else {
                    if (outputs[i].text !== translation) outputs[i].text = translation;
                    outputs[i].visible = !!translation;
                }
            }
        }
        function readView() {
            if (selected) {
                var label = selected.FindChildTraverse('DltShopTitle');
                if (label !== originals[0]) {
                    render([], null);
                    originals = label ? [label] : [];
                }
            }
            var texts = [read(0), read(1)];
            if (!toggle && !category && (!texts[1] || texts[0] === texts[1])) texts[0] = '';
            return texts;
        }
        var view = { root: root, visible: isVisible, read: readView, render: render };
        state.readers.push(view);
        if (toggle) {
            var original = root.FindChildTraverse(shop ? 'DltShopOriginal' : 'DltBuildOriginal');
            var progress = root.FindChildTraverse(shop ? 'DltShopProgress' : 'DltBuildProgress');
            var fill = progress.FindChildTraverse('DltBuildProgressFill');
            var status = progress.FindChildTraverse('DltBuildProgressText');
            var control = {
                root: root, visible: isVisible, request: send, receive: receive,
                cancel: function () { if (bridge.IsValid()) bridge.SetURL('about:blank'); },
                status: function (job, online) {
                    var running = job && (job.pending || job.queue.length);
                    var label = job && job.failed && !running ? '重试翻译' : '翻译当前出装';
                    if (toggle.text !== label) toggle.text = label;
                    original.visible = state.enabled;
                    original.text = running ? '取消翻译' : '显示原文';
                    progress.visible = state.enabled;
                    var percent = job && job.total ? Math.round(job.done * 100 / job.total) : 0;
                    var width = percent + '%';
                    if (fill.style.width !== width) fill.style.width = width;
                    var caption = state.error ? '出装界面读取异常，请重新打开商店' :
                        !online ? '后台翻译已停用' : !job ? '正在读取出装' : !job.total ?
                        '未读到出装文本' : running ?
                        (job.refining ? '精翻 ' : '翻译 ') + job.done + '/' + job.total :
                        job.failed ? '完成 ' + job.done + '/' + job.total + ' · ' + job.failed + ' 段失败' : '已翻译 ' + job.done + '/' + job.total;
                    if (status.text !== caption) status.text = caption;
                }
            };
            state.controls.push(control);
            root.__dltBuildTextAction = function (showOriginal) {
                var config = syncSettings();
                restartJob();
                state.clicks++;
                state.error = false;
                state.enabled = !showOriginal;
                state.owner = control;
                control.status(null, !config || config.enabled);
                if (showOriginal) state.readers.forEach(function (item) {
                    if (item.root.IsValid()) item.render([], null);
                });
            };
        }
        function send(job, batch, config) {
            var id = ++state.seq;
            job.pending = { id: id, batch: batch, deadline: Date.now() +
                (job.refining || !config ? (config && config.qualityTimeout || 90) + 5 :
                    Math.max(config.fastTimeout || 15, config.qualityTimeout || 90) + 5) * 1000 };
            eventTitle = '';
            try {
                bridge.SetURL('https://localhost:17866/build-text/__PAIRING__#' + encodeURIComponent(JSON.stringify({
                    id: id, texts: batch.texts, context: job.context, revision: state.revision, refine: job.refining
                })));
            } catch (_) { job.pending.deadline = 0; }
        }
        function receive() {
            try {
                var reply = eventTitle || String(bridge.title || '');
                if (reply.indexOf('DLTBUILD:') !== 0 && bridge.GetTitle) reply = String(bridge.GetTitle() || '');
                if (reply.indexOf('DLTBUILD:') === 0) return JSON.parse(reply.slice(9));
            } catch (_) {}
            return null;
        }
        function restartJob() {
            if (state.job && state.job.pending && state.owner) {
                try { state.owner.cancel(); } catch (_) {}
            }
            state.job = null;
            state.signature = null;
        }
        function syncSettings() {
            var config = scene.__dltBuildTextConfig;
            if (!config) return null;
            if (config.revision !== state.revision) { state.revision = config.revision; restartJob(); }
            if (state.autoEnabled !== config.autoEnabled) {
                state.autoEnabled = config.autoEnabled;
                state.enabled = config.autoEnabled === true;
                restartJob();
            }
            return config;
        }
        function attached(panel, parent) {
            for (var p = panel; p; p = p.GetParent()) if (p === parent) return true;
            return false;
        }
        function addBatches(queue, texts) {
            var batch = [], chars = 0;
            texts.forEach(function (text) {
                if (batch.length && (batch.length === 12 || chars + text.length > 3500)) {
                    queue.push({ texts: batch, attempts: 0, after: 0 }); batch = []; chars = 0;
                }
                batch.push(text); chars += text.length;
            });
            if (batch.length) queue.push({ texts: batch, attempts: 0, after: 0 });
        }
        function advance(job, config) {
            var now = Date.now();
            var pending = job.pending;
            if (pending) {
                var result = state.owner.receive();
                if (result && result.id === pending.id) {
                    job.pending = null;
                    if (result.error === 'settings-changed') { restartJob(); return; }
                    if (result.revision === state.revision && Array.isArray(result.translations) &&
                        result.translations.length === pending.batch.texts.length) {
                        pending.batch.texts.forEach(function (text, index) {
                            job.values[text] = result.translations[index];
                        });
                        job.done += pending.batch.texts.length;
                        if (result.refine && !job.refining) job.refine.push(pending.batch.texts);
                    } else retry(job, pending.batch, now, result.error === 'busy');
                } else if (now >= pending.deadline) {
                    state.owner.cancel();
                    job.pending = null;
                    retry(job, pending.batch, now, false);
                }
            }
            if (job.pending) return;
            if (!job.queue.length && job.refine.length) {
                job.refining = true;
                job.done = 0;
                job.total = job.refine.reduce(function (sum, batch) { return sum + batch.length; }, 0);
                job.refine.forEach(function (batch) { addBatches(job.queue, batch); });
                job.refine = [];
            }
            var next = job.queue.findIndex(function (batch) { return batch.after <= now; });
            if (next >= 0) state.owner.request(job, job.queue.splice(next, 1)[0], config);
        }
        function retry(job, batch, now, busy) {
            batch.attempts++;
            if (batch.attempts <= (busy ? 3 : 1)) {
                batch.after = now + (busy ? 1500 : 2500);
                job.queue.push(batch);
            } else job.failed += batch.texts.length;
        }
        function update() {
            var config = syncSettings();
            var online = !config || config.enabled;
            if (!state.owner || !state.owner.root.IsValid() || !state.owner.visible()) {
                var owner = state.controls.find(function (item) { return item.root.IsValid() && item.visible(); }) || null;
                if (owner !== state.owner) { restartJob(); state.owner = owner; }
            }
            if (state.owner && state.owner.root === root) {
                state.controls = state.controls.filter(function (item) { return item.root.IsValid(); });
                state.readers = state.readers.filter(function (item) { return item.root.IsValid(); });
                var views = state.readers.filter(function (item) {
                    return item.visible() && !state.controls.some(function (control) {
                        return control !== state.owner && attached(item.root, control.root) &&
                            !attached(control.root, root) && !attached(root, control.root);
                    });
                });
                var pairs = views.map(function (item) { return { view: item, texts: item.read() }; });
                var active = state.enabled && online;
                if (active) {
                    var base = pairs.filter(function (entry) { return attached(entry.view.root, root); });
                    var distinct = function (entries) {
                        return Array.from(new Set(entries.reduce(function (all, entry) { return all.concat(entry.texts); }, []).filter(Boolean)));
                    };
                    var baseTexts = distinct(base), texts = distinct(pairs);
                    var signature = JSON.stringify(baseTexts);
                    if (signature !== state.signature) {
                        restartJob();
                        state.signature = signature;
                        state.job = { context: baseTexts.join('\n').slice(0, config && config.contextChars || 6000),
                            known: Object.create(null), values: Object.create(null), queue: [], pending: null,
                            refine: [], refining: false, done: 0, total: 0, failed: 0 };
                    }
                    var job = state.job;
                    var fresh = texts.filter(function (value) { return !job.known[value]; }).slice(0, 96 - Object.keys(job.known).length);
                    fresh.forEach(function (value) { job.known[value] = true; });
                    job.total += fresh.length;
                    addBatches(job.queue, fresh);
                    advance(job, config);
                } else if (state.job) restartJob();
                pairs.forEach(function (entry) { entry.view.render(entry.texts, active && state.job ? state.job.values : null); });
                state.readers.forEach(function (item) { if (!item.visible()) item.render([], null); });
                state.controls.forEach(function (item) { if (item.visible()) item.status(state.job, online); });
            } else if (!state.owner) {
                render([], null);
            }
        }
        function tick() {
            if (!root.IsValid()) return;
            try {
                update();
                state.error = false;
            } catch (_) {
                state.error = true;
                state.controls.forEach(function (item) {
                    try { if (item.root.IsValid() && item.visible()) item.status(state.job, true); } catch (_) {}
                });
            }
            $.Schedule(toggle ? 0.15 : 0.4, tick);
        }
        $.Schedule(0, tick);
    }
})($.GetContextPanel());
