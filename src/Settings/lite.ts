const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const input = (id: string) => $<HTMLInputElement>(id);
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
const inGame = location.pathname.startsWith('/in-game/');
const loadId = new URLSearchParams(location.search).get('windowLoad') || '';
const windowLoadSuffix = () => /^\d{1,9}$/.test(loadId) ? ':load:' + loadId : '';
const backgroundSuffix = () => inGame ? ':background:static' : '';
const saveSuffix = () => ':save:' + (dirty || termsDirty ? 'dirty' : 'clean');
let settingsClosing = false, settingsInitialized = false, settingsSignal = 0;
let clipboardInProgress = false, settingsComposing = false;
let dirty = false, saving: Promise<void> | null = null, activePage = 'incoming';
let terms: Term[] = [], savedTerms: Term[] = [], termsTag = '', termsDirty = false, termsSaving = false;
let settingsRevision = '', current: Record<string, unknown> = {};
let saveTimer: ReturnType<typeof setTimeout> | undefined;

interface Window {
    DltSettingsNavigation?: {locateElement(element: HTMLElement): void};
}

class ApiError extends Error {
    constructor(message: string, public status: number) { super(message); }
}

function notice(message: string, error = false) {
    $('notice').textContent = message;
    $('notice').classList.toggle('error', error);
}

async function api(path: string, method = 'GET', value?: unknown) {
    const headers: Record<string, string> = {'Content-Type': 'application/json', 'X-DLT-Request': '1'};
    if (method === 'PUT' && path === 'config') headers['If-Match'] = '"' + settingsRevision + '"';
    if (method === 'PUT' && path === 'terms') headers['If-Match'] = termsTag;
    const bounded = path.startsWith('native-editor') || path.startsWith('hotkey/recording');
    const controller = bounded && typeof AbortController !== 'undefined' ? new AbortController() : null;
    const timer = controller ? setTimeout(() => controller.abort(), 5000) : undefined;
    try {
        const response = await fetch('/api/' + path, {
            method, headers, body: value === undefined ? undefined : JSON.stringify(value), signal: controller?.signal
        });
        const body = await response.json().catch(() => ({}));
        if (!response.ok) throw new ApiError(body.error || '设置后台暂不可用，请重试。', response.status);
        if (path !== 'terms') return body;
        const tag = response.headers.get('ETag') || '';
        if (method === 'GET') return {items: body, tag};
        termsTag = tag;
        return body;
    } finally { clearTimeout(timer); }
}

function bind(id: string, action: () => unknown | Promise<unknown>) {
    const button = $<HTMLButtonElement>(id);
    button.addEventListener('click', async () => {
        button.disabled = true;
        try { await action(); }
        catch (error) { notice((error as Error).message, true); }
        finally { button.disabled = false; }
    });
}

function publishSettingsState(state: 'ready' | 'error') {
    if (!inGame || settingsClosing) return;
    if (nativeEditorSession?.id) publishNativeGeometry(true);
    else document.title = 'DLTSETTINGS:' + state + ':' + (++settingsSignal) + backgroundSuffix() + saveSuffix() + windowLoadSuffix();
}

function markTermsDraft() {
    termsDirty = true;
    updateTermsHint();
    if (!termsSaving) setTermSaveStatus('词库有修改，请点击「保存词库」。');
}

const fields = [
    'enabled', 'incomingTargetLanguage', 'incomingCustomLanguage', 'incomingCustomLanguageCode', 'mode',
    'outgoingEnabled', 'outgoingTargetLanguage', 'outgoingCustomLanguage', 'outgoingCustomLanguageCode',
    'outgoingHotkey', 'outgoingMode', 'outgoingDisplayMode', 'llmBaseUrl', 'model'
];

const modifiers = ['Ctrl', 'Alt', 'Shift'];
const sendOptions = [...modifiers, 'Key'];
const chordKeys = [...'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789', 'Space', 'Enter', 'Tab',
    ...Array.from({length: 12}, (_, index) => 'F' + (index + 1))];
const chordLabels: Record<string, string> = {Space:'空格'};
for (const key of chordKeys) $<HTMLSelectElement>('sendKey').add(new Option(chordLabels[key] || key,key));
for (const name of modifiers) {
    const select = $<HTMLSelectElement>('send' + name);
    select.add(new Option('无',''));
    for (const [label, prefix] of [['任一',''], ['左','L'], ['右','R']])
        select.add(new Option(label + ' ' + name,prefix + name));
}

let recordingGeneration = 0, recordingId = '', recordingActive = false;
let recordingTimer: ReturnType<typeof setTimeout> | undefined;

function syncSendOptions(binding: string) {
    const pieces = binding.split('+');
    for (const name of modifiers)
        input('send' + name).value = pieces.slice(0,-1).find(piece => piece.endsWith(name)) || '';
    input('sendKey').value = pieces[pieces.length - 1];
}

function displaySendKey() {
    const saved = String(current.outgoingHotkey || 'LAlt+Space');
    const key = input('outgoingHotkey').value;
    $('sendKeyStatus').textContent = (current.outgoingEnabled === false ? '发送翻译已关闭。' : '') +
        '已保存 ' + saved + (saved === key ? '' : ' · 待保存 ' + key);
}

function stageSendKey(binding: string) {
    input('outgoingHotkey').value = binding;
    syncSendOptions(binding);
    dirty = true;
    displaySendKey();
    scheduleAutoSave();
}

for (const name of sendOptions) input('send' + name).addEventListener('change', () => {
    if (!settingsInitialized) return;
    void stopRecording();
    stageSendKey(sendOptions.map(part => input('send' + part).value).filter(Boolean).join('+'));
});

async function stopRecording() {
    const id = recordingId;
    recordingId = '';
    recordingActive = false;
    recordingGeneration++;
    clearTimeout(recordingTimer);
    $('recordSendKey').textContent = '录制快捷键';
    if (id) try { await api('hotkey/recording/' + id, 'DELETE'); } catch {}
}

async function pollRecording(id: string, generation: number) {
    if (settingsClosing || generation !== recordingGeneration) return;
    let result;
    try {
        result = await api('hotkey/recording/' + id);
    } catch (error) {
        if (generation !== recordingGeneration) return;
        void stopRecording();
        notice((error as Error).message,true);
        return;
    }
    if (generation !== recordingGeneration) return;
    if (result.state === 'completed' && typeof result.binding === 'string' && result.binding) {
        void stopRecording();
        stageSendKey(result.binding);
        await saveConfig().catch(error => notice(error.message,true));
        return;
    }
    if (result.state !== 'arming' && result.state !== 'recording') {
        void stopRecording();
        notice(result.error || '录制已结束。', Boolean(result.error));
        return;
    }
    notice(result.error || (result.state === 'arming' ? '先松开所有按键…'
        : '按下快捷键录制，剩余 ' + result.secondsRemaining + ' 秒。'));
    recordingTimer = setTimeout(() => void pollRecording(id,generation),250);
}

bind('recordSendKey', async () => {
    if (!settingsInitialized) return;
    if (recordingActive) { await stopRecording(); notice('已取消录制。'); return; }
    await commitNativeEditor();
    const generation = ++recordingGeneration;
    recordingActive = true;
    $('recordSendKey').textContent = '取消录制';
    try {
        const result = await api('hotkey/recording','POST',{inGame});
        if (typeof result.id !== 'string' || !/^[a-f0-9]{32}$/i.test(result.id)) throw new Error('录制未能启动，可使用下拉选项。');
        if (generation !== recordingGeneration) { await api('hotkey/recording/' + result.id,'DELETE'); return; }
        recordingId = result.id;
        void pollRecording(recordingId, generation);
    } catch (error) {
        if (generation !== recordingGeneration) return;
        await stopRecording();
        throw error;
    }
});
bind('resetSendKey', async () => { await stopRecording(); stageSendKey('LAlt+Space'); });
document.addEventListener('visibilitychange', () => { if (document.hidden) void stopRecording(); });
window.addEventListener('pagehide', () => void stopRecording());

function refreshVisibility() {
    for (const direction of ['incoming', 'outgoing'])
        $(direction + 'CustomFields').hidden = input(direction + 'TargetLanguage').value !== 'custom';
    const quality = activePage === 'incoming' ? input('mode').value === 'quality'
        : activePage === 'outgoing' && input('outgoingMode').value === 'quality';
    $('providerCard').hidden = !quality;
    const override = current.llmIndependentProfiles && current[activePage + 'Model'];
    const modelHint = $('providerModelHint');
    modelHint.hidden = !override;
    modelHint.textContent = override ? `当前${activePage === 'incoming' ? '接收' : '发送'}使用独立模型 ${override}，可在完整版更改。` : '';
}

function settingsValues() {
    const draft: Record<string, unknown> = {};
    for (const key of fields) {
        const element = input(key);
        draft[key] = key === 'outgoingDisplayMode' ? (element.checked ? 'bilingual' : 'translation')
            : element.type === 'checkbox' ? element.checked : element.value.trim();
    }
    return draft;
}

function llmPresetFields(values: Record<string, unknown>) {
    if (values.llmBaseUrl === current.llmBaseUrl && values.model === current.model) return {};
    const address = String(values.llmBaseUrl);
    const presetModels = ['deepseek-flash', 'deepseek-v4-pro'];
    const useDeepSeekPreset = /^https:\/\/api\.deepseek\.com(?::443)?(?:\/v1)?(?:\/chat\/completions|\/)?$/i.test(address)
        && current.llmPreset !== 'custom'
        && [values.model, current.incomingModel, current.outgoingModel].every(name => !name || presetModels.includes(String(name)));
    return useDeepSeekPreset ? {llmPreset: 'deepseek'}
        : {llmPreset: 'custom', llmCustomBaseUrl: values.llmBaseUrl, llmCustomModel: values.model};
}

async function saveConfig() {
    if (!settingsInitialized) return;
    if (settingsComposing) throw new Error('请先完成输入法选词。');
    await commitNativeEditor();
    if (saving) await saving;
    if (!dirty) return;
    const values = settingsValues(), key = input('llmKey').value.trim();
    saving = (async () => {
        try {
            const result = await api('config', 'PUT', {...current, ...values, ...llmPresetFields(values), ...(key ? {llmKey: key} : {})});
            current = result.settings;
            settingsRevision = result.revision;
            if (input('outgoingHotkey').value === values.outgoingHotkey) {
                input('outgoingHotkey').value = String(result.settings.outgoingHotkey);
                values.outgoingHotkey = result.settings.outgoingHotkey;
                syncSendOptions(String(values.outgoingHotkey));
            }
            displaySendKey();
            if (key && input('llmKey').value.trim() === key) input('llmKey').value = '';
            input('llmKey').placeholder = result.hasLlmKey ? '已保存 · 留空保持不变' : '填写密钥';
            dirty = JSON.stringify(settingsValues()) !== JSON.stringify(values) || !!input('llmKey').value.trim();
            notice(dirty ? '已保存，后续修改待写入。' : '翻译设置已保存。');
            publishSettingsState('ready');
        } finally { saving = null; }
    })();
    await saving;
}

function scheduleAutoSave() {
    clearTimeout(saveTimer);
    if (!dirty || settingsClosing) return;
    saveTimer = setTimeout(() => {
        if (settingsComposing || nativeEditorSession || document.activeElement instanceof HTMLInputElement) return;
        void saveConfig().catch(error => notice(error.message, true));
    }, 500);
}

for (const event of ['input', 'change']) document.addEventListener(event, e => {
    const target = e.target as HTMLElement;
    if (!settingsInitialized || clipboardInProgress) return;
    if (target.closest('#termRows')) { markTermsDraft(); return; }
    if (fields.includes(target.id) || target.id === 'llmKey') {
        dirty = true;
        refreshVisibility();
        scheduleAutoSave();
    }
});
document.addEventListener('focusout', scheduleAutoSave);
document.addEventListener('compositionstart', () => { settingsComposing = true; });
document.addEventListener('compositionend', () => { settingsComposing = false; scheduleAutoSave(); });

async function leaveSettings() {
    await stopRecording();
    await saveConfig();
    if (termsDirty || termsSaving) throw new Error('请先保存或撤销词库修改。');
    if (!inGame) { window.close(); return; }
    settingsClosing = true;
    document.title = 'DLTSETTINGS:close' + windowLoadSuffix();
}

bind('saveLite', saveConfig);
bind('closeLite', leaveSettings);
document.addEventListener('keydown', event => {
    if (!inGame || event.key !== 'Escape') return;
    event.preventDefault();
    if (nativeEditorSession) void cancelNativeEditor();
    else if (recordingActive) void stopRecording();
    else void leaveSettings().catch(error => notice(error.message, true));
});
bind('discardTermsDraft', async () => {
    await cancelNativeEditor();
    terms = clone(savedTerms);
    termsDirty = false;
    $('termConflicts').hidden = true;
    drawTerms();
    setTermSaveStatus('已恢复到上次保存的词库。');
});

bind('pasteLiteKey', async () => {
    const result = await api('settings/paste-secret', 'POST', {field: 'llmKey'});
    input('llmKey').value = result.value;
    dirty = true;
    notice('密钥已粘贴，请点击「保存翻译设置」。');
});

function renderUpdate(view: {currentVersion: string; version?: string; available: boolean; checkError?: string; lastChecked?: string; publishedVersion?: string}) {
    $('installedLiteVersion').textContent = view.currentVersion;
    let status = '尚未检查更新';
    if (view.checkError) status = view.checkError;
    else if (view.available) status = '可更新到精简版 ' + view.version;
    else if (view.publishedVersion) status = '已发布精简版 ' + view.publishedVersion + '，暂无可用更新。';
    else if (view.lastChecked) status = 'GitHub 暂无精简版安装包。';
    $('releaseState').textContent = status;
    $('openUpdate').hidden = !view.available;
    $('updateBadge').hidden = !view.available;
}
bind('checkUpdates', async () => {
    $('releaseState').textContent = '正在检查精简版更新…';
    try { renderUpdate(await api('updates/check', 'POST')); }
    catch (error) { $('releaseState').textContent = (error as Error).message; }
});
bind('openUpdate', () => api('updates/open/lite', 'POST'));
bind('bilibili', () => api('community/bilibili', 'POST'));
bind('github', () => api('community/github', 'POST'));

const pages = Array.from(document.querySelectorAll<HTMLButtonElement>('[data-lite-page]'));
bind('updateBadge', () => pages.find(button => button.dataset.litePage === 'author')?.click());
for (const button of pages) button.addEventListener('click', async () => {
    try {
        await commitNativeEditor();
        if (activePage === 'outgoing' && button.dataset.litePage !== 'outgoing') await stopRecording();
        activePage = button.dataset.litePage!;
        for (const entry of pages) {
            const active = entry === button;
            entry.classList.toggle('active', active);
            $(entry.dataset.litePage!).hidden = !active;
        }
        $('pageTitle').textContent = button.textContent;
        $('contentScroll').scrollTop = 0;
        refreshVisibility();
        if (activePage === 'author') renderUpdate(await api('updates'));
    } catch (error) { notice((error as Error).message, true); }
});

void Promise.all([api('config'), api('terms'), api('languages'), api('updates')]).then(([settings, glossary, languages, update]) => {
    for (const direction of ['incoming', 'outgoing']) {
        const select = $<HTMLSelectElement>(direction + 'TargetLanguage');
        for (const language of languages) select.add(new Option(language.name, language.id));
        select.add(new Option('自定义语言', 'custom'));
    }
    current = settings.settings;
    settingsRevision = settings.revision;
    for (const key of fields) {
        const element = input(key);
        if (key === 'outgoingDisplayMode') element.checked = current[key] === 'bilingual';
        else if (element.type === 'checkbox') element.checked = Boolean(current[key]);
        else element.value = String(current[key] ?? '');
    }
    input('llmKey').placeholder = settings.hasLlmKey ? '已保存 · 留空保持不变' : '填写密钥';
    syncSendOptions(input('outgoingHotkey').value);
    displaySendKey();
    terms = glossary.items;
    termsTag = glossary.tag;
    savedTerms = clone(terms);
    drawTerms();
    refreshVisibility();
    renderUpdate(update);
    if (update.available) notice('同频精简版 ' + update.version + ' 已发布，在「作者信息」查看。');
    $('connection').textContent = '后台已连接';
    settingsInitialized = true;
    publishSettingsState('ready');
    const updateTimer = setInterval(() => {
        if (!settingsClosing && !document.hidden) void api('updates').then(renderUpdate).catch(() => {});
    }, 30000);
    window.addEventListener('pagehide', () => clearInterval(updateTimer), {once:true});
}).catch(error => { notice(error.message, true); publishSettingsState('error'); });
