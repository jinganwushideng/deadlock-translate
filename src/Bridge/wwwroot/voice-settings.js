(() => {
    const card = document.getElementById('teammateVoiceCard');
    const find = id => document.getElementById(id);
    const form = find('voiceForm');
    const model = find('voiceModel');
    const backend = find('voiceBackend');
    const draftSwitch = find('voiceLightweightDraft');
    const draftDownload = find('voiceDraftDownload');
    const draftStatus = find('voiceDraftStatus');
    const draftModel = 'base-q5_1';
    const cudaDownload = find('voiceCudaDownload');
    const cudaCancel = find('voiceCudaCancel');
    const cudaProgress = find('voiceCudaProgress');
    const enabledSwitch = find('voiceEnabled');
    const testSwitch = find('voiceTestMode');
    const saveButton = find('voiceSave');
    const note = find('voiceStatus');
    const field = id => find(id).value;
    let loaded = false;
    let saving = false;
    let busy = false;
    let edited = false;
    let editRevision = 0;
    let saveRevision = 0;
    let pendingMessage = '';
    let errorMessage = '';
    let cudaPending = false;
    let cudaError = '';
    let modelOptionsKey = '';
    let info;
    const states = {
        off: '已关闭', waiting: '等待进入对局', loading: '正在加载并预热模型',
        ready: '已就绪 · 队友发言时识别', unavailable: '语音组件不可用', error: '语音组件需要检查'
    };
    const deviceNames = { cuda: 'CUDA', vulkan: 'Vulkan GPU', cpu: 'CPU' };
    async function request(path, method = 'GET', data) {
        const reply = await fetch('/api/voice' + path, {
            method, credentials: 'same-origin',
            headers: { 'X-DLT-Request': '1', 'Content-Type': 'application/json' },
            body: data === undefined ? undefined : JSON.stringify(data)
        });
        if (!reply.ok) throw new Error((await reply.json().catch(() => ({}))).error || '无法访问语音设置');
        return reply.status === 202 || reply.status === 204 || path === '/cancel-download' ? null : reply.json();
    }
    function refreshCuda() {
        const cuda = info?.cuda;
        const active = !!cuda?.phase;
        const missing = info?.supported && cuda?.availability === 'missing';
        find('voiceCuda').hidden = backend.value !== 'cuda';
        cudaDownload.hidden = !missing || active;
        cudaDownload.disabled = cudaPending;
        cudaCancel.hidden = !active;
        cudaCancel.disabled = cudaPending;
        cudaProgress.hidden = !active;
        if (cuda?.phase === 'downloading') {
            cudaProgress.max = cuda.total;
            cudaProgress.value = cuda.received;
        } else cudaProgress.removeAttribute('value');
        const states = {
            missing: `缺少 cuBLAS · 从 NVIDIA 下载约 ${Math.round((cuda?.total || 0) / 1000000)} MB`,
            driver: '当前 NVIDIA 显卡或驱动无法使用 CUDA 13，可使用 Vulkan GPU',
            ready: info?.settings.backend === 'cuda' ? 'CUDA 组件已就绪' : 'CUDA 组件已就绪 · 保存语音设置后启用',
            unavailable: '无法检测 CUDA 组件，可使用 Vulkan GPU'
        };
        const status = cuda?.phase === 'verifying' ? '正在校验并安装 CUDA 组件…' :
            active ? `正在下载 CUDA 组件 · ${Math.floor(cuda.received / 1000000)} / ${Math.round(cuda.total / 1000000)} MB` :
            cudaError || cuda?.error || states[cuda?.availability] || '正在检测 CUDA 组件…';
        find('voiceCudaStatus').textContent = info?.supported ? status : states.unavailable;
    }
    function refreshControls() {
        const custom = model.value === 'custom';
        const download = info?.download;
        const installed = info?.models.find(item => item.id === model.value)?.installed;
        find('voiceCustomRow').hidden = !custom;
        const wantsDraft = draftSwitch.checked;
        const baseInstalled = info?.models.find(item => item.id === draftModel)?.installed;
        draftDownload.hidden = !wantsDraft || model.value === draftModel || baseInstalled && info?.draftMode !== 'fallback';
        draftDownload.disabled = busy || saving || !!download?.active;
        draftDownload.textContent = baseInstalled ? '重新下载 Base 草稿模型' : '下载 Base 草稿模型（60 MB）';
        find('voiceDownloadRow').hidden = custom && draftDownload.hidden;
        find('voiceDownloadActions').hidden = custom && draftDownload.hidden && !download?.active;
        find('voiceDownload').hidden = custom;
        draftStatus.hidden = !wantsDraft;
        if (wantsDraft) {
            draftStatus.textContent = download?.active && download.model === draftModel ? '正在下载 Base 草稿模型…' :
                !edited && model.value === draftModel && info?.draftMode === 'fallback' ? '设备较忙，当前显示 Base 的完整识别结果。' :
                model.value === draftModel ? '草稿和最终字幕共用所选的 Base 模型。' :
                !baseInstalled ? '下载 Base 后在下一局出草稿，最终字幕仍由所选模型校正。' :
                edited ? '保存后启用 Base 草稿，最终字幕由所选模型校正。' :
                info?.draftMode === 'base' ? 'Base 草稿已生效，最终字幕由所选模型校正。' :
                info?.draftMode === 'fallback' ? '当前由所选模型出字幕。Base 草稿将在下次进入对局时重新检查。' :
                '进入对局后预热 Base 草稿模型。';
        }
        enabledSwitch.disabled = saving || !loaded;
        testSwitch.disabled = saving || !loaded;
        saveButton.disabled = saving || !loaded;
        enabledSwitch.setAttribute('aria-checked', String(info?.settings.enabled === true));
        testSwitch.setAttribute('aria-checked', String(info?.settings.testMode === true));
        find('voiceDownload').disabled = busy || !!download?.active;
        find('voiceDownload').textContent = installed ? '重新下载' : '下载所选模型';
        find('voiceCancel').hidden = !download?.active;
        find('voiceProgress').hidden = !download?.active;
        if (download?.active) {
            find('voiceProgress').max = download.total;
            find('voiceProgress').value = download.received;
        }
        const device = deviceNames[info?.activeBackend];
        const fallback = device && info.settings.backend !== info.activeBackend ? 'CUDA 不可用 · ' : '';
        const runtimeStatus = device && info.status === 'loading' ? `${fallback}正在用 ${device} 加载模型` :
            device && info.status === 'ready' ? `${fallback}已就绪 · 使用 ${device} 识别队友语音` : states[info?.status];
        note.textContent = pendingMessage || errorMessage || download?.error || info?.problem ||
            (info ? (!info.supported ? '需要支持进程音频采集的 Windows 和完整语音组件。' : runtimeStatus || '等待后台') : '正在读取语音设置…');
        refreshCuda();
    }
    function render(data) {
        info = data;
        const selected = edited ? model.value : data.settings.model;
        const labels = { 'base-q5_1': '较轻量', 'small-q5_1': '短句均衡', 'medium-q5_0': '较大', 'large-v3-turbo-q5_0': '较大' };
        const key = data.models.map(item => item.id + ':' + item.installed).join('|');
        if (key !== modelOptionsKey) {
            modelOptionsKey = key;
            model.replaceChildren();
            for (const item of data.models) {
                const size = Math.round(item.bytes / 1000000);
                model.add(new Option(item.name + ' · ' + labels[item.id] + ' · ' + size + ' MB' + (item.installed ? ' · 已下载' : ''), item.id));
            }
            model.add(new Option('自选本地 .bin', 'custom'));
        }
        model.value = selected;
        if (!edited) {
            draftSwitch.checked = data.settings.lightweightDraft === true;
            for (const [id, property] of [['voiceBackend', 'backend'], ['voiceLanguage', 'language'], ['voiceTranslation', 'translation']])
                find(id).value = data.settings[property];
            if (document.activeElement !== find('voiceCustomPath')) find('voiceCustomPath').value = data.settings.customPath;
        }
        loaded = true;
        form.hidden = false;
        refreshControls();
    }
    async function refresh() {
        if (busy || saving || !card.getClientRects().length) return;
        busy = true;
        const revision = saveRevision;
        try {
            const data = await request(backend.value === 'cuda' ? '?checkCuda=true' : '');
            if (revision === saveRevision && !saving) {
                if (!info) errorMessage = '';
                render(data);
            }
        }
        catch (error) { if (revision === saveRevision && !saving) errorMessage = error.message; }
        finally { busy = false; refreshControls(); }
    }
    function markDraft() {
        edited = true;
        editRevision++;
        refreshControls();
    }
    form.addEventListener('input', markDraft);
    form.addEventListener('change', markDraft);
    backend.addEventListener('change', () => { cudaError = ''; void refresh(); });
    async function save(data, keepDraft, message) {
        if (!loaded || saving) return;
        saving = true;
        saveRevision++;
        const submittedEdit = editRevision;
        pendingMessage = message;
        errorMessage = '';
        refreshControls();
        try {
            const updated = await request('', 'PATCH', data);
            if (!keepDraft && submittedEdit === editRevision) edited = false;
            render(updated);
        } catch (error) { errorMessage = error.message; }
        finally { saving = false; pendingMessage = ''; refreshControls(); }
    }
    enabledSwitch.addEventListener('click', () => {
        const enabled = info?.settings.enabled !== true;
        void save({ enabled }, true, enabled ? '正在开启队友语音…' : '正在关闭队友语音…');
    });
    testSwitch.addEventListener('click', () => {
        const testMode = info?.settings.testMode !== true;
        void save({ testMode }, true, testMode ? '正在开启语音测试…' : '正在关闭语音测试…');
    });
    function savePreferences() {
        void save({
            model: model.value, customPath: field('voiceCustomPath'),
            backend: field('voiceBackend'), language: field('voiceLanguage'), translation: field('voiceTranslation'),
            lightweightDraft: draftSwitch.checked
        }, false, '正在保存语音设置…');
    }
    saveButton.addEventListener('click', savePreferences);
    form.addEventListener('submit', event => { event.preventDefault(); savePreferences(); });
    async function downloadModel(id) {
        if (info?.download.active || !id || id === 'custom') return;
        try {
            await request('/download', 'POST', { model: id, mirror: field('voiceMirror') });
            await refresh();
        } catch (error) { note.textContent = error.message; }
    }
    find('voiceDownload').addEventListener('click', () => void downloadModel(model.value));
    draftDownload.addEventListener('click', () => void downloadModel(draftModel));
    find('voiceCancel').addEventListener('click', async () => {
        try { await request('/cancel-download', 'POST'); await refresh(); }
        catch (error) { note.textContent = error.message; }
    });
    async function cudaAction(path) {
        if (cudaPending) return;
        cudaPending = true;
        cudaError = '';
        refreshCuda();
        try {
            await request(path, 'POST');
            await refresh();
        } catch (error) { cudaError = error.message; }
        finally { cudaPending = false; refreshCuda(); }
    }
    cudaDownload.addEventListener('click', () => void cudaAction('/cuda/download'));
    cudaCancel.addEventListener('click', () => void cudaAction('/cuda/cancel'));
    const matrix = document.createElement('details');
    const summary = document.createElement('summary');
    summary.textContent = '查看模型与设备实测';
    matrix.append(summary);
    find('voiceBenchmark').append(matrix);
    matrix.addEventListener('toggle', async () => {
        if (!matrix.open || matrix.querySelector('table')) return;
        try {
            const data = await fetch('/voice-benchmark.json').then(reply => {
                if (!reply.ok) throw new Error();
                return reply.json();
            });
            const table = document.createElement('table');
            const addRow = (cells, heading = false) => {
                const row = table.insertRow();
                for (const value of cells) {
                    const cell = document.createElement(heading ? 'th' : 'td');
                    if (heading) cell.scope = 'col';
                    cell.textContent = value;
                    row.append(cell);
                }
            };
            const note = document.createElement('p');
            note.className = 'hint';
            note.textContent = data.hardware + '。合成中英短句的识别中位数，单位为秒。CUDA 13 需 NVIDIA CUDA Toolkit。';
            addRow(['模型', 'CPU', 'CUDA 13', 'Vulkan', '样本观察'], true);
            const seconds = value => value === null ? '整批超时' : value.toFixed(2);
            for (const item of data.models)
                addRow([item.name, seconds(item.seconds.cpu), seconds(item.seconds.cuda), seconds(item.seconds.vulkan), item.note]);
            matrix.append(note, table);
        } catch {
            const error = document.createElement('p');
            error.textContent = '暂时无法读取实测数据。';
            matrix.append(error);
        }
    });
    setInterval(refresh, 2000);
    refresh();
})();
