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
        if (root.__dltBuildTextInitialized) return;
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
        root.__dltBuildTextInitialized = true;

        var scene = root;
        while (scene.GetParent()) scene = scene.GetParent();
        var state = scene.__dltBuildText;
        if (!state) {
            state = { owner: null, controls: [], readers: [], clicks: 0, error: false, enabled: false, autoEnabled: null,
                revision: '', seq: 0, job: null, signature: null, completed: Object.create(null), completedOrder: [],
                forceCollect: true, nextCollectAt: 0, pairs: [], collectionMs: 0, collections: 0,
                clickAt: 0, collectDelayMs: null, dispatchDelayMs: null };
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
        var applied = [null, null], rendered = null;
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
            var last = label.__dltBuildOriginal || applied[index];
            if (last && last.panel === label && last.translation === text) text = last.source;
            else if (last) rendered = null;
            text = text.trim();
            return text.length <= 6000 && !/^[#{]/.test(text) && /[\p{L}]/u.test(text) ? text : '';
        }
        function render(texts, values) {
            var next = [originals[0], originals[1], texts[0], texts[1], values && values[texts[0]] || '', values && values[texts[1]] || ''];
            if (rendered && next.every(function (value, index) { return value === rendered[index]; })) return;
            rendered = next;
            for (var i = 0; i < originals.length; i++) {
                var label = originals[i];
                if (!label || !label.IsValid()) continue;
                var translation = values && values[texts[i]] || '';
                if (category || shop) {
                    var last = label.__dltBuildOriginal || applied[i];
                    if (translation) {
                        var originalHtml = last && last.panel === label ? last.html : label.html;
                        label.html = false;
                        if (label.text !== translation) label.text = translation;
                        applied[i] = { panel: label, source: texts[i], translation: translation, html: originalHtml };
                        label.__dltBuildOriginal = applied[i];
                    } else if (last && last.panel === label) {
                        if (typeof last.html === 'boolean') label.html = last.html;
                        if (label.text === last.translation) label.text = last.source;
                        applied[i] = null;
                        label.__dltBuildOriginal = null;
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
        state.forceCollect = true;
        if (toggle) {
            var original = root.FindChildTraverse(shop ? 'DltShopOriginal' : 'DltBuildOriginal');
            var progress = root.FindChildTraverse(shop ? 'DltShopProgress' : 'DltBuildProgress');
            var fill = progress.FindChildTraverse('DltBuildProgressFill');
            var status = progress.FindChildTraverse('DltBuildProgressText');
            var control = {
                root: root, visible: isVisible, request: send, receive: receive,
                cancel: function () { if (bridge.IsValid()) bridge.SetURL('about:blank'); },
                status: function (job, online) {
                    var running = !!(job && (job.pending || job.queue.length));
                    var label = job && job.failed && !running ? '重试翻译' : '翻译当前出装';
                    if (toggle.text !== label) toggle.text = label;
                    original.visible = state.enabled;
                    original.text = running ? '取消翻译' : '显示原文';
                    var now = Date.now();
                    progress.visible = state.enabled && (!job || !job.total || running || job.failed || !job.finishedAt || now - job.finishedAt < 3500);
                    var percent = job && job.total ? Math.round(job.done * 100 / job.total) : 0;
                    var width = percent + '%';
                    if (fill.style.width !== width) fill.style.width = width;
                    var looking = job && (job.pending && job.pending.batch.lookup || job.queue.some(function (batch) { return batch.lookup; }));
                    var caption = state.error ? '出装界面读取异常，请重新打开商店' :
                        !online ? '后台翻译已停用' : !job ? '正在读取出装' : !job.total ?
                        '未读到出装文本' : running ?
                        (looking ? '读取本地译文 ' : job.refining ? '精翻 ' : '云端翻译 ') + job.done + '/' + job.total :
                        job.failed ? '完成 ' + job.done + '/' + job.total + ' · ' + job.failed + ' 段失败' :
                        (job.translated ? '已翻译 ' : '已读取本地译文 ') + job.done + '/' + job.total;
                    if (job && job.total) caption += ' · 本地 ' + job.cacheHits + ' · 新译 ' + job.translated;
                    if (status.text !== caption) status.text = caption;
                }
            };
            state.controls.push(control);
            root.__dltBuildTextAction = function (showOriginal) {
                var config = syncSettings();
                state.clicks++;
                state.error = false;
                if (!showOriginal && state.enabled && state.owner === control && state.job &&
                    (state.job.pending || state.job.queue.length)) {
                    control.status(state.job, !config || config.enabled);
                    return;
                }
                restartJob();
                state.enabled = !showOriginal;
                state.owner = control;
                state.clickAt = Date.now();
                state.collectDelayMs = null;
                state.dispatchDelayMs = null;
                control.status(null, !config || config.enabled);
                if (showOriginal) state.readers.forEach(function (item) {
                    if (item.root.IsValid()) item.render([], null);
                });
                else $.Schedule(0, function () {
                    if (root.IsValid() && state.owner === control && state.enabled) refresh();
                });
            };
        }
        function send(job, batch, config) {
            var id = ++state.seq;
            if (state.clickAt && state.dispatchDelayMs === null) state.dispatchDelayMs = Date.now() - state.clickAt;
            job.pending = { id: id, batch: batch, deadline: Date.now() +
                (job.refining || !config ? (config && config.qualityTimeout || 90) + 5 :
                    Math.max(config.fastTimeout || 15, config.qualityTimeout || 90) + 5) * 1000 };
            eventTitle = '';
            try {
                bridge.SetURL('https://localhost:17866/build-text/__PAIRING__#' + encodeURIComponent(JSON.stringify({
                    id: id, texts: batch.texts, context: job.context, revision: state.revision, refine: job.refining,
                    build: job.build, cacheOnly: !!batch.lookup
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
            state.forceCollect = true;
        }
        function syncSettings() {
            var config = scene.__dltBuildTextConfig;
            if (!config) return null;
            if (config.revision !== state.revision) {
                state.revision = config.revision; restartJob();
                state.completed = Object.create(null); state.completedOrder = [];
            }
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
        function addBatches(queue, texts, lookup) {
            var batch = [], chars = 0;
            texts.forEach(function (text) {
                if (batch.length && (batch.length === (lookup ? 48 : 12) || chars + text.length > (lookup ? 10000 : 3500))) {
                    queue.push({ texts: batch, attempts: 0, after: 0, lookup: !!lookup }); batch = []; chars = 0;
                }
                batch.push(text); chars += text.length;
            });
            if (batch.length) queue.push({ texts: batch, attempts: 0, after: 0, lookup: !!lookup });
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
                        result.translations.length === pending.batch.texts.length &&
                        result.translations.every(function (text) { return typeof text === 'string' || pending.batch.lookup && text === null; })) {
                        var misses = [], refinement = [];
                        pending.batch.texts.forEach(function (text, index) {
                            var value = result.translations[index];
                            if (value === null) { misses.push(text); return; }
                            job.values[text] = value;
                            job.done++;
                            var refine = result.refine || result.refineIndices && result.refineIndices.indexOf(index) >= 0;
                            job.memo[text] = { value: value, refine: !!refine };
                            if (refine && !job.refining) refinement.push(text);
                        });
                        job.cacheHits += result.cacheHits || 0;
                        job.translated += result.translated || 0;
                        addBatches(job.queue, misses);
                        if (refinement.length) job.refine.push(refinement);
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
            if (!job.pending && !job.queue.length && !job.refine.length && !job.finishedAt) job.finishedAt = now;
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
                var active = state.enabled && online;
                var collect = state.forceCollect || Date.now() >= state.nextCollectAt;
                if (collect) {
                    var started = Date.now();
                    state.controls = state.controls.filter(function (item) { return item.root.IsValid(); });
                    state.readers = state.readers.filter(function (item) { return item.root.IsValid(); });
                    var views = state.readers.filter(function (item) {
                        return item.visible() && !state.controls.some(function (control) {
                            return control !== state.owner && attached(item.root, control.root) &&
                                !attached(control.root, root) && !attached(root, control.root);
                        });
                    });
                    state.pairs = views.map(function (item) { return { view: item, texts: item.read() }; });
                    var pairs = state.pairs;
                    if (active) {
                        var base = pairs.filter(function (entry) { return attached(entry.view.root, root); });
                        var distinct = function (entries) {
                            return Array.from(new Set(entries.reduce(function (all, entry) { return all.concat(entry.texts); }, []).filter(Boolean)));
                        };
                        var baseTexts = distinct(base), texts = distinct(pairs);
                        var title = read(0);
                        var build = title.replace(/<[^>]*>/g, '').replace(/\[[^\[\]\r\n]*%[^\[\]\r\n]*\]/g, '').replace(/\s+/g, ' ').trim().slice(0, 1000);
                        var signature = build || JSON.stringify(baseTexts.slice().sort());
                        if (signature !== state.signature) {
                            restartJob();
                            state.signature = signature;
                            var memo = state.completed[signature];
                            if (!memo) {
                                if (state.completedOrder.length >= 8) delete state.completed[state.completedOrder.shift()];
                                state.completedOrder.push(signature);
                                memo = state.completed[signature] = Object.create(null);
                            }
                            state.job = { context: '', build: build, memo: memo,
                                known: Object.create(null), values: Object.create(null), queue: [], pending: null,
                                refine: [], refining: false, done: 0, total: 0, failed: 0, cacheHits: 0, translated: 0, finishedAt: 0 };
                        }
                        var job = state.job;
                        job.context = baseTexts.join('\n').slice(0, config && config.contextChars || 6000);
                        var fresh = texts.filter(function (value) { return !job.known[value]; }).slice(0, 96 - Object.keys(job.known).length);
                        var unseen = [];
                        fresh.forEach(function (value) {
                            job.known[value] = true;
                            var saved = job.memo[value];
                            if (!saved) { unseen.push(value); return; }
                            job.values[value] = saved.value; job.done++; job.cacheHits++;
                            if (saved.refine) job.refine.push([value]);
                        });
                        job.total += fresh.length;
                        if (fresh.length) job.finishedAt = 0;
                        addBatches(job.queue, unseen, true);
                    }
                    state.forceCollect = false;
                    state.nextCollectAt = Date.now() + 500;
                    state.collectionMs = Date.now() - started;
                    state.collections++;
                    if (state.clickAt && state.collectDelayMs === null) state.collectDelayMs = Date.now() - state.clickAt;
                    state.readers.forEach(function (item) { if (!item.visible()) item.render([], null); });
                }
                if (active && state.job) advance(state.job, config);
                else if (state.job) restartJob();
                state.pairs.forEach(function (entry) {
                    if (entry.view.root.IsValid()) entry.view.render(entry.texts, active && state.job ? state.job.values : null);
                });
                state.controls.forEach(function (item) { if (item.visible()) item.status(state.job, online); });
            } else if (!state.owner) {
                render([], null);
            }
        }
        function refresh() {
            try {
                update();
                state.error = false;
            } catch (_) {
                state.error = true;
                state.controls.forEach(function (item) {
                    try { if (item.root.IsValid() && item.visible()) item.status(state.job, true); } catch (_) {}
                });
            }
        }
        function tick() {
            if (!root.IsValid()) return;
            refresh();
            $.Schedule(toggle && state.job && state.job.pending ? 0.05 : toggle ? 0.15 : 0.4, tick);
        }
        $.Schedule(0, tick);
    }
})($.GetContextPanel());
