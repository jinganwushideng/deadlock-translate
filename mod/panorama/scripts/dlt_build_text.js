function DLTBuildTextAction() {
    for (var panel = $.GetContextPanel(); panel; panel = panel.GetParent()) {
        if (panel.__dltBuildTextAction) {
            panel.__dltBuildTextAction();
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
        var tooltip = !toggle && !category;
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
                recent: Object.create(null), background: new Map(),
                forceCollect: true, nextCollectAt: 0, pairs: [], collectionMs: 0, collections: 0,
                clickAt: 0, collectDelayMs: null, dispatchDelayMs: null,
                sentParts: 0, receivedParts: 0, maxReplyChars: 0, decodeErrors: 0, timeouts: 0 };
            scene.__dltBuildText = state;
        }
        var priorWire = { sent: 0, received: 0, errors: 0 };
        var transport = bridge ? DLTBuildTransport(bridge, function (stats) {
            state.sentParts += stats.sent - priorWire.sent;
            state.receivedParts += stats.received - priorWire.received;
            state.decodeErrors += stats.errors - priorWire.errors;
            state.maxReplyChars = Math.max(state.maxReplyChars, stats.maximum);
            priorWire = { sent: stats.sent, received: stats.received, errors: stats.errors };
        }) : null;
        var applied = [null, null], rendered = null, selectedRevision = 0;
        function normalize(text) {
            return text.replace(/\s+/g, ' ').trim();
        }
        function titleKey(text) {
            return normalize(text.replace(/<\/?(?:b|i|u|font|span|strong|em|br)(?:\s[^<>]*)?\s*\/?>/gi, ' ')
                .replace(/\[\s*\d+(?:[.,]\d+)?\s*%\s*(?:WR|win\s*rate|胜率)\s*\]/gi, ' '))
                .replace(/\s*\[/g, '[').toUpperCase();
        }
        function translationFor(job, source, title) {
            if (!job || !source) return '';
            if (Object.prototype.hasOwnProperty.call(job.values, source)) return job.values[source];
            if (title && job.planTitle && titleKey(source) === titleKey(job.planTitle)) return job.values[job.planTitle] || '';
            var key = normalize(source);
            return Object.prototype.hasOwnProperty.call(job.normalized, key) ? job.normalized[key] : '';
        }
        function isVisible() {
            for (var panel = root; panel; panel = panel.GetParent()) {
                if (panel.visible === false || panel.style.visibility === 'collapse' ||
                    panel.style.visibility === 'hidden' || panel.BHasClass && panel.BHasClass('Hidden')) return false;
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
        function render(texts, job) {
            if (job && !tooltip && (category || shop) && texts.some(function (text, index) { return read(index) !== text; })) {
                state.forceCollect = true;
                return;
            }
            var translations = [translationFor(job, texts[0], !!toggle), translationFor(job, texts[1], false)];
            var next = [originals[0], originals[1], texts[0], texts[1], translations[0], translations[1]];
            if (rendered && next.every(function (value, index) { return value === rendered[index]; })) return;
            rendered = next;
            for (var i = 0; i < originals.length; i++) {
                var label = originals[i];
                if (!label || !label.IsValid()) continue;
                var translation = translations[i];
                if (category || shop || toggle) {
                    if (outputs[i] && outputs[i].IsValid()) outputs[i].visible = false;
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
                    if (outputs[i] && outputs[i].IsValid()) {
                        if (outputs[i].text !== translation) outputs[i].text = translation;
                        outputs[i].visible = !!translation;
                    }
                }
            }
        }
        function readView() {
            if (selected) {
                var label = selected.FindChildTraverse('DltShopTitle');
                if (!label && selected.FindChildrenWithClassTraverse) {
                    var titles = selected.FindChildrenWithClassTraverse('SelectedBuildName');
                    if (titles.length === 1) label = titles[0];
                }
                if (label !== originals[0]) {
                    render([], null);
                    originals = label ? [label] : [];
                    selectedRevision++;
                }
            }
            var texts = [read(0), read(1)];
            if (tooltip) texts[0] = '';
            return texts;
        }
        var view = { root: root, category: !!category, visible: isVisible, read: readView, render: render };
        if (!tooltip) state.readers.push(view);
        state.forceCollect = true;
        if (toggle) {
            var progress = root.FindChildTraverse(shop ? 'DltShopProgress' : 'DltBuildProgress');
            var fill = progress.FindChildTraverse('DltBuildProgressFill');
            var status = progress.FindChildTraverse('DltBuildProgressText');
            var control = {
                root: root, visible: isVisible, request: send, receive: receive, reopening: false, kind: button ? 'details' : 'shop',
                cancel: function () { if (bridge.IsValid()) transport.cancel(); },
                status: function (job, online) {
                    var running = !!(job && (job.bg ? !job.finishedAt : job.pending || job.queue.length));
                    var label = state.enabled ? '显示原文' : '翻译当前出装';
                    DLTSetButtonCaption(toggle, label);
                    var now = Date.now();
                    var needsTranslation = job && (job.bg ? job.working && !job.finishedAt :
                        job.pending && isTranslation(job.pending.batch) || job.queue.some(isTranslation));
                    var confirmation = job && !job.reopened && job.translated && job.finishedAt && now - job.finishedAt < 3500;
                    progress.visible = state.owner === control && state.enabled && online && !!(needsTranslation || job && (job.manualCapture || job.failed || confirmation));
                    var percent = job && job.total ? Math.round(job.done * 100 / job.total) : 0;
                    var width = percent + '%';
                    if (fill.style.width !== width) fill.style.width = width;
                    var looking = job && (job.pending && job.pending.batch.lookup || job.queue.some(function (batch) { return batch.lookup; }));
                    var sourceErrors = { 'account-unavailable': '未找到当前 Steam 账号', 'cache-missing': '本地出装缓存尚未生成，请先加载该方案',
                        'cache-too-large': '本地出装缓存超出读取限制', 'cache-changing': '游戏正在保存出装，请稍后重试',
                        'cache-unreadable': '本地出装缓存无法读取，请重新加载该方案', 'not-found': '本地没有此方案，请在游戏中加载后重试',
                        'ambiguous': '正在核对同名方案', 'mismatch': '正在核对当前方案内容',
                        'capture-expired': '读取超时，请重新点击翻译', 'export-format': '游戏未导出完整方案，请重试',
                        'selection-changed': '方案已切换，请重新点击翻译', 'clipboard-busy': '剪贴板正在使用，请重试',
                        'unavailable': '读取整套出装失败，请重试' };
                    var caption = state.error ? '出装界面读取异常，请重新打开商店' :
                        !online ? '后台翻译已停用' : state.awaitingBuild ? '等待游戏加载出装名称' : !job ? '正在读取出装' : job.manualCapture ?
                        '请在出装选项中点击「复制到剪贴板」' : !job.total ?
                        (sourceErrors[job.sourceStatus] || (job.failed ? '读取整套出装失败，请重试' : '正在读取本地整套出装')) : running ?
                        (looking ? '读取本地译文 ' : job.refining ? '精翻 ' : '后台翻译 ') + job.done + '/' + job.total :
                        job.failed ? '完成 ' + job.done + '/' + job.total + ' · ' + job.failed + ' 段失败' :
                        (job.translated ? '已翻译 ' : '已读取本地译文 ') + job.done + '/' + job.total;
                    if (job && job.total) caption += ' · 缓存 ' + job.cacheHits + ' · 新译 ' + job.translated;
                    if (job && job.visibleOnly) caption += ' · 当前页';
                    if (status.text !== caption) status.text = caption;
                }
            };
            state.controls.push(control);
            root.__dltBuildTextAction = function () {
                var config = syncSettings();
                var showOriginal = state.enabled;
                var background = state.job && state.job.bg;
                state.error = false;
                state.clicks++;
                restartJob();
                state.enabled = !showOriginal;
                state.owner = control;
                state.clickAt = showOriginal ? 0 : Date.now();
                state.collectDelayMs = null;
                state.dispatchDelayMs = null;
                if (showOriginal && background && (!background.ended || background.failed) && !(config && config.sourceExports)) {
                    state.background.delete(background.identity);
                    if (!background.ended) control.request({ bg: background }, { texts: [], action: 'cancel' }, config);
                }
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
            var input = encodeURIComponent(JSON.stringify({
                id: id, texts: batch.texts, context: batch.lookup || batch.plan ? '' : job.context,
                revision: state.revision, refine: job.refining, build: job.build,
                cacheOnly: !!batch.lookup, loadPlan: !!batch.plan, buildId: job.buildId || null, includeSnapshot: false,
                groups: batch.plan ? job.groups : null, description: batch.plan ? job.description : null, fingerprint: job.fingerprint || null,
                job: job.bg ? job.bg.key : null, action: batch.action, cursor: job.cursor || 0,
                parentJob: batch.action === 'note' ? job.parentKey : null,
                source: job.bg ? job.bg.source || job.bg.captureToken : null, buildVersion: job.buildVersion || null
            }));
            var seconds = batch.action || batch.lookup ? 10 : batch.plan ? 30 :
                Math.max(config && config.fastTimeout || 15, config && config.qualityTimeout || 90) + 5;
            job.pending = { id: id, batch: batch, deadline: Date.now() + seconds * 1000 + Math.ceil(input.length / 1200) * 150 };
            try { transport.send(id, input); }
            catch (_) { job.pending.deadline = 0; }
        }
        function receive(pending) {
            var received = state.receivedParts;
            var result = transport.read(pending.id);
            if (received !== state.receivedParts) pending.deadline = Math.max(pending.deadline, Date.now() + 5000);
            return result;
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
                state.recent = Object.create(null);
                state.background.clear();
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
        function isTranslation(batch) {
            return !batch.plan && !batch.lookup && !batch.action;
        }
        function makeBackground(identity) {
            var saved = state.background.get(identity);
            if (saved && !saved.failed) return saved;
            var source = saved && saved.source;
            var key = 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'.replace(/x/g, function () {
                return Math.floor(Math.random() * 16).toString(16);
            });
            saved = { key: key, identity: identity, accepted: false, ended: false, source: source };
            if (state.background.size >= 12) state.background.delete(state.background.keys().next().value);
            state.background.set(identity, saved);
            return saved;
        }
        function advanceBackground(job, config) {
            if (job.finishedAt) return;
            var now = Date.now();
            if (job.pending) {
                var pending = job.pending;
                var reply = state.owner.receive(pending);
                if (!reply && now < pending.deadline) return;
                job.pending = null;
                if (!reply) {
                    state.timeouts++;
                    job.pollAfter = now + 750;
                    return;
                }
                if (reply.error === 'settings-changed') { restartJob(); return; }
                if (reply.error === 'job-missing') {
                    if (job.parentKey) { job.failed = 1; job.finishedAt = now; return; }
                    job.bg.accepted = false;
                    job.bg.ended = false;
                    job.cursor = 0;
                    job.pollAfter = now + 250;
                    return;
                }
                if (reply.error === 'busy') { job.pollAfter = now + 1000; return; }
                if (reply.error === 'source-expired') {
                    job.bg.source = null; job.bg.captureToken = null; job.pollAfter = now + 100; return;
                }
                if (reply.error && !Array.isArray(reply.fields)) {
                    job.failed = reply.failed || 1;
                    job.finishedAt = now;
                    job.bg.ended = true;
                    job.bg.failed = true;
                    return;
                }
                if (pending.batch.action === 'source-prepare' || pending.batch.action === 'source-read') {
                    var capture = reply.capture;
                    if (capture && capture.token) {
                        job.bg.captureToken = capture.token;
                        job.manualCapture = capture.manual === true;
                        try { if (!job.manualCapture) $.DispatchEvent('CitadelCopySelectedBuildContentsToClipboard', 0); }
                        catch (_) { job.failed = 1; job.finishedAt = now; }
                    } else if (capture && capture.source) {
                        job.bg.source = capture.source;
                        job.bg.captureToken = null;
                        job.manualCapture = false;
                    } else if (capture && capture.pending) {
                        // Clipboard export can take several native frames.
                    } else if (!capture || capture.error) {
                        job.failed = 1;
                        job.sourceStatus = capture && capture.error || 'export-format';
                        job.finishedAt = now;
                        job.bg.failed = true;
                    }
                    job.pollAfter = now + 100;
                    return;
                }
                if (pending.batch.action === 'start' || pending.batch.action === 'note') {
                    job.bg.accepted = reply.accepted === true;
                    job.pollAfter = now + 250;
                    return;
                }
                if (reply.plan) {
                    job.fingerprint = reply.plan.fingerprint;
                    job.planTitle = reply.plan.name;
                    job.planGroups = Array.isArray(reply.plan.groups) ? groupKeys(reply.plan.groups) : null;
                    job.visibleOnly = reply.plan.visibleOnly;
                }
                if (!Array.isArray(reply.fields) || !Number.isInteger(reply.cursor)) {
                    job.pollAfter = now + 750;
                    return;
                }
                reply.fields.forEach(function (field) {
                    if (typeof field.source === 'string' && typeof field.value === 'string') storeValue(job, field.source, field.value);
                });
                job.cursor = reply.cursor;
                job.total = reply.total;
                job.done = reply.done;
                job.cacheHits = reply.cacheHits;
                job.translated = reply.translated;
                job.failed = reply.failed;
                job.working = reply.working;
                if (reply.ended && !reply.more) {
                    job.finishedAt = now;
                    if (reply.error) job.failed = Math.max(1, job.failed);
                    job.bg.ended = true;
                    job.bg.failed = !!job.failed;
                    return;
                }
                job.pollAfter = now + (reply.more ? 0 : 350);
            }
            if (now < job.pollAfter) return;
            var action = job.bg.accepted ? 'poll' : job.parentKey ? 'note' : 'start';
            if (config && config.sourceExports && !job.bg.source && !job.parentKey)
                action = job.bg.captureToken ? 'source-read' : 'source-prepare';
            state.owner.request(job, { action: action, plan: action === 'start',
                texts: action === 'poll' || action === 'source-read' ? [] : job.sources }, config);
        }
        function advanceHoveredNote(job, config) {
            if (config && config.sourceExports) return;
            if (!job.bg || !job.finishedAt || !job.fingerprint) return;
            var hovered = state.hoveredNote;
            if (!job.note && hovered && hovered.job === job && Date.now() - hovered.since >= 200 &&
                !Object.prototype.hasOwnProperty.call(job.normalizedSource, normalize(hovered.text))) {
                if (!job.attemptedNotes) job.attemptedNotes = new Set();
                if (!job.attemptedNotes.has(hovered.text) && job.attemptedNotes.size < 48) {
                    job.attemptedNotes.add(hovered.text);
                    var identity = JSON.stringify(['annotation', job.bg.key, hovered.text]);
                    job.note = { bg: makeBackground(identity), parentKey: job.bg.key, sources: [hovered.text],
                        values: Object.create(null), normalized: Object.create(null), normalizedSource: Object.create(null),
                        cursor: 0, pollAfter: 0 };
                }
            }
            if (!job.note) return;
            advanceBackground(job.note, config);
            if (job.note.finishedAt) {
                Object.keys(job.note.values).forEach(function (text) { storeValue(job, text, job.note.values[text]); });
                job.note = null;
            }
        }
        function rememberComplete(job) {
            if (!job || !job.fingerprint || !job.finishedAt || job.failed || !job.total || job.done !== job.total) return;
            // Without a native ID, require the complete visible group structure before restoring a verified plan.
            if (!job.buildId && (!job.structure.length ||
                job.planGroups && !containsGroups(job.structure, job.planGroups))) return;
            state.recent[shop ? 'shop' : 'details'] = job;
        }
        function storeValue(job, text, value) {
            job.values[text] = value;
            var key = normalize(text);
            var existing = job.normalizedSource[key];
            if (existing === undefined || existing === text) {
                job.normalizedSource[key] = text;
                job.normalized[key] = value;
            } else {
                delete job.normalized[key];
            }
        }
        function loadFields(job, texts, fingerprint, config) {
            job.fingerprint = fingerprint || null;
            job.context = texts.join('\n').slice(0, config && config.contextChars || 6000);
            var memoKey = fingerprint;
            if (memoKey) {
                job.memo = state.completed[memoKey];
                if (!job.memo) {
                    if (state.completedOrder.length >= 8) delete state.completed[state.completedOrder.shift()];
                    state.completedOrder.push(memoKey);
                    job.memo = state.completed[memoKey] = Object.create(null);
                }
            }
            var unseen = [];
            Array.from(new Set(texts)).forEach(function (text) {
                job.total++;
                var saved = job.memo[text];
                if (!saved) { unseen.push(text); return; }
                storeValue(job, text, saved.value); job.done++; job.cacheHits++;
                if (saved.refine) job.refine.push([text]);
            });
            addBatches(job.queue, unseen, true);
        }
        function groupKeys(groups) {
            return groups.map(function (group) { return JSON.stringify([normalize(group.name), normalize(group.note)]); }).sort();
        }
        function containsGroups(all, subset) {
            var i = 0;
            for (var j = 0; j < all.length && i < subset.length; j++) {
                if (all[j] === subset[i]) i++;
            }
            return i === subset.length;
        }
        function advance(job, config) {
            var now = Date.now();
            var pending = job.pending;
            if (pending) {
                var result = state.owner.receive(pending);
                if (result && result.id === pending.id) {
                    job.pending = null;
                    if (result.error === 'settings-changed') { restartJob(); return; }
                    if (pending.batch.plan && result.revision === state.revision && result.planStatus) {
                        job.sourceStatus = result.planStatus;
                        var plan = result.plan;
                        if (result.planStatus === 'ready' && plan && typeof plan.name === 'string' &&
                            Array.isArray(plan.texts) && plan.texts.length > 0 && plan.texts.length <= 256 &&
                            plan.texts.every(function (text) { return typeof text === 'string' && text.length <= 6000; }) &&
                            plan.texts.reduce(function (size, text) { return size + text.length; }, 0) <= 64000) {
                            job.build = plan.name.slice(0, 1000);
                            job.planTitle = plan.name;
                            job.planGroups = Array.isArray(plan.groups) ? groupKeys(plan.groups) : null;
                            var visible = pending.batch.texts;
                            var present = new Set(plan.texts.map(normalize));
                            var extra = visible.filter(function (text) {
                                return !present.has(normalize(text)) && titleKey(text) !== titleKey(plan.name);
                            });
                            // Put the title and on-screen groups first when the engine returns batches slowly.
                            var order = new Set([normalize(plan.name)].concat(visible.map(normalize)));
                            var first = plan.texts.filter(function (text) { return order.has(normalize(text)); });
                            var rest = plan.texts.filter(function (text) { return !order.has(normalize(text)); });
                            loadFields(job, first.concat(extra, rest), plan.fingerprint, config);
                        } else if (['not-found','mismatch','ambiguous','cache-missing','cache-changing','cache-unreadable','account-unavailable','cache-too-large'].indexOf(result.planStatus) >= 0 &&
                            typeof result.fallback === 'string' && /^[0-9a-f]{64}$/i.test(result.fallback)) {
                            if (pending.batch.attempts === 0 && ['not-found','mismatch','cache-changing'].indexOf(result.planStatus) >= 0) {
                                pending.batch.attempts++;
                                pending.batch.after = now + 500;
                                job.queue.push(pending.batch);
                            } else {
                                job.visibleOnly = true;
                                loadFields(job, pending.batch.texts, result.fallback, config);
                            }
                        } else { job.failed = 1; job.sourceStatus = sourceErrorsStatus(result.planStatus); }
                    } else if (!pending.batch.plan && result.revision === state.revision && Array.isArray(result.translations) &&
                        result.translations.length === pending.batch.texts.length &&
                        result.translations.every(function (text) { return typeof text === 'string' || pending.batch.lookup && text === null; })) {
                        var misses = [], refinement = [];
                        pending.batch.texts.forEach(function (text, index) {
                            var value = result.translations[index];
                            if (value === null) { misses.push(text); return; }
                            storeValue(job, text, value);
                            job.done++;
                            var refine = result.refine || result.refineIndices && result.refineIndices.indexOf(index) >= 0;
                            job.memo[text] = { value: value, refine: !!refine };
                            if (refine && !job.refining) refinement.push(text);
                        });
                        job.cacheHits += result.cacheHits || 0;
                        job.translated += result.translated || 0;
                        if(pending.batch.lookup && result.snapshot && typeof result.snapshot === 'object'){
                            Object.keys(result.snapshot).slice(0,256).forEach(function (text) {
                                var saved = result.snapshot[text];
                                if(saved && typeof saved.value === 'string' && saved.value.length<=4096 &&
                                    (!job.memo[text] || !saved.refine || job.memo[text].refine))
                                    job.memo[text] = { value: saved.value, refine: !!saved.refine };
                            });
                        }
                        addBatches(job.queue, misses);
                        if (refinement.length) job.refine.push(refinement);
                    } else retry(job, pending.batch, now, result.error === 'busy');
                } else if (now >= pending.deadline) {
                    state.timeouts++;
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
        function sourceErrorsStatus(status) {
            return typeof status === 'string' && status.length <= 40 ? status : 'unavailable';
        }
        function retry(job, batch, now, busy) {
            batch.attempts++;
            if (batch.attempts <= (busy ? 3 : 1)) {
                batch.after = now + (busy ? 1500 : 2500);
                job.queue.push(batch);
            } else { job.failed += batch.texts.length; if (batch.plan) job.sourceStatus = 'unavailable'; }
        }
        function update() {
            var config = syncSettings();
            var online = !config || config.enabled;
            var visibleControls = state.controls.filter(function (item) { return item.root.IsValid() && item.visible(); });
            var preferred = visibleControls.find(function (item) { return item.kind === 'details'; }) || visibleControls[0] || null;
            if (!state.owner || !state.owner.root.IsValid() || !state.owner.visible() ||
                preferred && preferred.kind === 'details' && state.owner.kind !== 'details') {
                var owner = preferred;
                if (owner !== state.owner) {
                    restartJob();
                    state.owner = owner;
                    if (owner) owner.reopening = true;
                }
            }
            if (state.owner && state.owner.root === root) {
                var active = state.enabled && online;
                var collect = state.forceCollect || Date.now() >= state.nextCollectAt;
                if (collect) {
                    var started = Date.now();
                    state.controls = state.controls.filter(function (item) { return item.root.IsValid(); });
                    state.readers = state.readers.filter(function (item) { return item.root.IsValid(); });
                    var views = state.readers.filter(function (item) {
                        return (item.visible() || item.category && attached(item.root, root)) && !state.controls.some(function (control) {
                            return control !== state.owner && attached(item.root, control.root) &&
                                !attached(control.root, root) && !attached(root, control.root);
                        });
                    });
                    state.pairs = views.map(function (item) { return { view: item, texts: item.read() }; });
                    var pairs = state.pairs;
                    if (active) {
                        state.awaitingBuild = !read(0);
                        if (state.awaitingBuild && state.job) restartJob();
                        if (!state.awaitingBuild) {
                            var base = pairs.filter(function (entry) { return attached(entry.view.root, root); });
                            var distinct = function (entries) {
                                return Array.from(new Set(entries.reduce(function (all, entry) { return all.concat(entry.texts); }, []).filter(Boolean)));
                            };
                            var title = read(0);
                            var build = title.slice(0, 1000);
                            var idLabels = root.FindChildrenWithClassTraverse ? root.FindChildrenWithClassTraverse('BuildID') : [];
                            var idText = idLabels.length ? String(idLabels[0].text || '').replace(/<[^>]*>/g, '') : '';
                            var idParts = idText.match(/\d+/g);
                            var buildId = idParts && idParts.length === 1 && Number(idParts[0]) <= 4294967295 ? Number(idParts[0]) : null;
                            var versions = root.FindChildrenWithClassTraverse ? root.FindChildrenWithClassTraverse('Version') : [];
                            var versionParts = versions.length ? String(versions[0].text || '').match(/\d+/g) : null;
                            var buildVersion = versionParts && versionParts.length === 1 ? Number(versionParts[0]) : null;
                            var groups = base.filter(function (entry) { return entry.view.category; }).map(function (entry) {
                                return { name: normalize(entry.texts[0]), note: normalize(entry.texts[1]) };
                            }).filter(function (group) { return group.name || group.note; });
                            var description = read(1);
                            var structure = groupKeys(groups);
                            var identity = JSON.stringify([titleKey(build), buildId, buildVersion, buildId ? 0 : selectedRevision, normalize(description), structure]);
                            var selection = JSON.stringify([titleKey(build), buildId, selectedRevision, normalize(description)]);
                            var signature = JSON.stringify([selection, structure]);
                            if (signature !== state.signature) {
                                var previous = state.job;
                                // Late Panorama panels can fill in a plan that is already verified.
                                // A different group, replaced panel/title or changed description starts a fresh job.
                                if (previous && previous.selection === selection && previous.planGroups &&
                                    containsGroups(structure, previous.structure) && containsGroups(previous.planGroups, structure)) {
                                    previous.structure = structure;
                                    previous.identity = identity;
                                    state.signature = signature;
                                } else {
                                    restartJob();
                                    state.signature = signature;
                                    var recent = state.recent[shop ? 'shop' : 'details'];
                                    if (state.owner.reopening && recent && recent.identity === identity) {
                                        state.job = Object.assign({}, recent, { selection: selection, translated: 0,
                                            cacheHits: Object.keys(recent.values).length });
                                    } else {
                                        var textSize = 0;
                                        var visible = [title].concat(distinct(base).filter(function (text) { return text !== title; })).slice(0, 96)
                                            .filter(function (text) { textSize += text.length; return textSize <= 48000; });
                                        var groupSize = 0;
                                        var observed = groups.slice(0, 96).filter(function (group) { groupSize += group.name.length + group.note.length; return groupSize <= 48000; });
                                        var background = config && config.backgroundJobs ? makeBackground(identity) : null;
                                        state.job = { context: '', build: build, buildId: buildId, buildVersion: buildVersion, groups: observed, description: description,
                                            selection: selection, identity: identity, structure: structure, memo: Object.create(null), sourceStatus: '',
                                            values: Object.create(null), normalized: Object.create(null), normalizedSource: Object.create(null),
                                            bg: background, sources: visible, cursor: 0, pollAfter: 0, reopened: !!(background && background.accepted),
                                            queue: background ? [] : [{ texts: visible, plan: true, attempts: 0, after: 0 }], pending: null,
                                            refine: [], refining: false, done: 0, total: 0, failed: 0, cacheHits: 0, translated: 0, finishedAt: 0 };
                                    }
                                    state.owner.reopening = false;
                                }
                            }
                        }
                    }
                    state.forceCollect = false;
                    state.nextCollectAt = Date.now() + (state.awaitingBuild ? 50 : 500);
                    state.collectionMs = Date.now() - started;
                    state.collections++;
                    if (!state.awaitingBuild && state.clickAt && state.collectDelayMs === null) state.collectDelayMs = Date.now() - state.clickAt;
                    state.readers.forEach(function (item) {
                        if (!item.visible() && !(item.category && attached(item.root, root))) item.render([], null);
                    });
                }
                if (active && state.job) {
                    if (state.job.bg) advanceBackground(state.job, config);
                    else advance(state.job, config);
                    if (state.job) advanceHoveredNote(state.job, config);
                    rememberComplete(state.job);
                }
                else if (state.job) restartJob();
                state.pairs.forEach(function (entry) {
                    if (entry.view.root.IsValid()) entry.view.render(entry.texts, active ? state.job : null);
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
            if (tooltip) {
                var visible = isVisible() && state.enabled && state.owner && state.owner.visible();
                var job = visible ? state.job : null;
                var texts = job ? readView() : [];
                render(texts, job);
                var hovered = state.hoveredNote;
                if (job && texts[1]) {
                    if (!hovered || hovered.root !== root || hovered.job !== job || hovered.text !== texts[1])
                        state.hoveredNote = { root: root, job: job, text: texts[1], since: Date.now() };
                } else if (hovered && hovered.root === root) state.hoveredNote = null;
                $.Schedule(visible ? 0.04 : 0.10, tick);
                return;
            }
            refresh();
            $.Schedule(toggle && (state.awaitingBuild || state.job && state.job.pending) ? 0.05 : toggle ? 0.15 : 0.4, tick);
        }
        $.Schedule(0, tick);
    }
})($.GetContextPanel());
