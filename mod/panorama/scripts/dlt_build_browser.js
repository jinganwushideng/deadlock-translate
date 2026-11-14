function DLTBuildListAction() {
    var panel = $.GetContextPanel();
    while (panel) {
        if (panel.__dltBuildListAction) { panel.__dltBuildListAction(); return; }
        panel = panel.GetParent();
    }
}
(function (root) {
    var list = root.FindChildTraverse('HeroBuildList');
    var button = root.FindChildTraverse('DltListTranslate');
    var bridge = root.FindChildTraverse('DltListBridge');
    var status = root.FindChildTraverse('DltListStatus');
    if (!list || !button || !bridge || root.__dltListInitialized) return;
    root.__dltListInitialized = true;
    var scene = root;
    while (scene.GetParent()) scene = scene.GetParent();
    var wire = DLTBuildTransport(bridge);
    var enabled = false, rows = [], signature = '', generation = 0, sequence = 0;
    var job = null, pending = null, changed = false;
    function setCaption(text) {
        DLTSetButtonCaption(button, text);
    }
    function showOutput(row, value) {
        row.output.visible = !!value;
        row.output.style.visibility = value ? 'visible' : 'collapse';
    }
    function collect() {
        var panels = list.FindChildrenWithClassTraverse('HeroBuildListItem');
        return panels.map(function (panel) {
            var label = panel.FindChildTraverse('DltListName');
            var output = panel.FindChildTraverse('DltListNameTranslation');
            return label && output ? { panel: panel, label: label, output: output, source: String(label.text || '').trim() } : null;
        }).filter(function (row) { return row && row.source && row.source.length <= 1000; });
    }
    function request(action, config) {
        var id = ++sequence;
        var texts = action === 'list' ? Array.from(new Set(rows.map(function (row) { return row.source; }))).slice(0, 512) : [];
        pending = { id: id, action: action, deadline: Date.now() + 15000 };
        wire.send(id, encodeURIComponent(JSON.stringify({ id: id, job: job.key, action: action, texts: texts,
            context: '', revision: config.revision, build: 'Build browser list', cursor: job.cursor })));
    }
    root.__dltBuildListAction = function () {
        if (!rows.length) { status.text = '当前没有可翻译的出装'; return; }
        if (enabled && !changed) { enabled = false; setCaption('翻译当前列表'); rows.forEach(function (row) { showOutput(row, false); }); return; }
        enabled = true; changed = false;
        job = { key: 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'.replace(/x/g, function () { return Math.floor(Math.random() * 16).toString(16); }),
            generation: generation, cursor: 0, values: Object.create(null), accepted: false, ended: false, after: 0 };
        setCaption('显示原文');
    };
    function tick() {
        if (!root.IsValid()) return;
        var visible = true;
        for (var ancestor = root; ancestor; ancestor = ancestor.GetParent())
            if (ancestor.visible === false || ancestor.BHasClass && ancestor.BHasClass('Hidden')) { visible = false; break; }
        if (visible) {
            var config = scene.__dltBuildTextConfig;
            var observed = collect();
            var search = root.FindChildTraverse('SearchTextEntry');
            var next = JSON.stringify([search && search.text || '', observed.map(function (row) { return [row.panel.id, row.source]; })]);
            if (next !== signature) {
                rows.forEach(function (row) { if (row.output.IsValid()) showOutput(row, false); });
                observed.forEach(function (row) { showOutput(row, false); });
                signature = next; generation++; rows = observed;
                if (job) { job = null; pending = null; wire.cancel(); changed = true; setCaption('翻译新结果'); }
            }
            rows = observed;
            if (enabled && job && config && config.enabled) {
                if (pending) {
                    var response = wire.read(pending.id);
                    if (response) {
                        if (pending.action === 'list') {
                            job.accepted = response.accepted === true;
                            if (!job.accepted) status.text = '后台任务繁忙，正在等待';
                        }
                        else if (Array.isArray(response.fields)) {
                            response.fields.forEach(function (field) { job.values[field.source] = field.value; });
                            job.cursor = response.cursor;
                            job.ended = response.ended && !response.more;
                            status.text = response.ended ? '列表翻译完成' : '列表翻译 ' + response.done + '/' + response.total;
                        }
                        if (response.error) { status.text = '列表翻译失败，请重新点击'; job.ended = true; }
                        pending = null; job.after = Date.now() + 300;
                    } else if (Date.now() > pending.deadline) { pending = null; job.after = Date.now() + 700; }
                }
                if (!pending && !job.ended && Date.now() >= job.after) request(job.accepted ? 'poll' : 'list', config);
                rows.forEach(function (row) {
                    var value = job.values[row.source];
                    row.output.text = value || '';
                    showOutput(row, value);
                });
            }
        }
        $.Schedule(0.15, tick);
    }
    $.Schedule(0, tick);
})($.GetContextPanel());
