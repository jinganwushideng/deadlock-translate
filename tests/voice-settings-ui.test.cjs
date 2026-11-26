const assert = require('node:assert/strict');
const { randomBytes } = require('node:crypto');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require('playwright');

const repo = path.resolve(__dirname, '..');
const { version } = require('../version.json');
const runtime = process.env.DLT_TEST_RUNTIME || path.join(repo, `dist/DeadlockTranslate-${version}`);
const fixtures = path.join(repo, 'artifacts/voice-settings-ui');
fs.mkdirSync(fixtures, { recursive: true });
const state = fs.mkdtempSync(path.join(fixtures, 'fixture-'));
const key = randomBytes(24).toString('hex');
fs.writeFileSync(path.join(state, 'pairing.key'), key);
const voiceSettings = path.join(state, 'voice-settings.json');
fs.writeFileSync(voiceSettings, JSON.stringify({ testMode: true }));

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const port = () => new Promise(resolve => {
    const server = net.createServer();
    server.listen(0, '127.0.0.1', () => {
        const actual = server.address().port;
        server.close(() => resolve(actual));
    });
});

(async () => {
    const http = await port(), tls = await port(), base = 'http://localhost:'+http;
    const child = spawn(path.join(runtime, 'DeadlockTranslate.exe'),
        ['--no-shell', '--no-hotkey', '--data-dir', state, '--pairing-file', path.join(state, 'pairing.key'),
            '--port', String(http), '--https-port', String(tls)],
        { cwd: runtime, windowsHide: true, stdio: 'ignore' });
    let browser, page, releasePoll;
    try {
        for (let tries = 0; ; tries++) {
            try { if ((await fetch(base+'/health')).ok) break; } catch {}
            assert(tries < 150 && child.exitCode === null, 'isolated backend starts');
            await delay(100);
        }
        browser = await chromium.launch({ channel: 'msedge', headless: true });
        page = await browser.newPage({ viewport: { width: 1600, height: 920 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const openVoice = async () => {
            await page.locator('nav [data-page="incoming"]').click();
            await page.locator('nav [data-section="teammateVoiceCard"]').click();
            await page.waitForSelector('#voiceForm:visible');
        };
        await page.goto(base+'/in-game/'+key);
        await openVoice();
        const voiceSwitch = page.getByRole('switch', { name: '启用队友语音' });
        const switchState = () => voiceSwitch.getAttribute('aria-checked');
        const saved = () => page.evaluate(async () => (await (await fetch('/api/voice')).json()).settings);
        const patchReply = () => page.waitForResponse(reply =>
            reply.url().endsWith('/api/voice') && reply.request().method() === 'PATCH' && reply.ok());
        const clickOnly = () => voiceSwitch.evaluate(node => node.dispatchEvent(
            new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 })));

        assert.equal(await switchState(), 'false');
        assert.equal(await page.locator('#voiceModel option').count(), 5);
        const testMode = page.getByRole('switch', { name: '队友语音测试模式' });
        assert.equal(await testMode.getAttribute('aria-checked'), 'false', 'an opted-in test build starts the official backend with diagnostics off');
        assert.match(await page.locator('#teammateVoiceCard').innerText(), /模型越大.*占用越多/);
        const enableTest = patchReply();
        await testMode.click();
        assert.deepEqual((await enableTest).request().postDataJSON(), {testMode: true});
        await page.waitForFunction(() => document.getElementById('voiceTestMode').getAttribute('aria-checked') === 'true');
        assert.equal((await saved()).testMode, true);
        assert.equal(await switchState(), 'false', 'test mode does not start teammate audio by itself');
        let capturePoll;
        const oldReply = new Promise(resolve => { releasePoll = resolve; });
        const pollCaptured = new Promise(resolve => { capturePoll = resolve; });
        let captureOne = true;
        await page.route('**/api/voice', async route => {
            if (!captureOne || route.request().method() !== 'GET') return route.continue();
            captureOne = false;
            const response = await route.fetch();
            capturePoll();
            await oldReply;
            await route.fulfill({ response });
        });
        try {
            await Promise.race([pollCaptured, delay(6500).then(() => assert.fail('no old poll captured'))]);
            const opened = patchReply();
            await clickOnly();
            const response = await opened;
            assert.deepEqual(response.request().postDataJSON(), { enabled: true });
            await page.waitForFunction(() => document.getElementById('voiceEnabled').getAttribute('aria-checked') === 'true');
            assert.equal((await saved()).enabled, true);
            assert.equal(await page.locator('#voiceStatus').textContent(), '等待进入对局');
            const pollArrived = page.waitForResponse(reply => reply.url().endsWith('/api/voice') &&
                reply.request().method() === 'GET');
            releasePoll();
            await pollArrived;
            await delay(150);
            assert.equal(await switchState(), 'true', 'a delayed status response cannot undo the switch');
        } finally {
            releasePoll();
            await page.unroute('**/api/voice');
        }

        await page.locator('#voiceModel').selectOption('medium-q5_0');
        await page.locator('#voiceBackend').selectOption('cuda');
        const closed = patchReply();
        await clickOnly();
        await closed;
        await page.waitForFunction(() => document.getElementById('voiceEnabled').getAttribute('aria-checked') === 'false');
        const off = await saved();
        assert.equal(off.enabled, false);
        assert.equal(off.model, 'small-q5_1', 'the switch updates only enabled');
        assert.equal(await page.locator('#voiceModel').inputValue(), 'medium-q5_0', 'draft selection survives switching');
        assert.equal(await page.locator('#voiceBackend').inputValue(), 'cuda');
        const stored = patchReply();
        await page.locator('#voiceSave').click();
        await stored;
        assert.equal(JSON.parse(fs.readFileSync(voiceSettings,'utf8')).testMode,false,'voice preferences keep the diagnostic HUD off at next startup');
        await page.reload();
        await openVoice();
        assert.equal(await switchState(), 'false');
        assert.equal(await page.locator('#voiceModel').inputValue(), 'medium-q5_0');
        assert.equal(await page.locator('#voiceBackend').inputValue(), 'cuda');

        const cudaButton = page.getByRole('button', { name: '下载 CUDA 加速组件' });
        const cudaProgress = page.locator('#voiceCudaProgress');
        let cuda = { availability: 'driver', total: 401229263 };
        let downloadClicks = 0;
        await page.route(/\/api\/voice(?:\?checkCuda=true|\/cuda\/download)$/, async route => {
            if (route.request().method() === 'POST') {
                downloadClicks++;
                cuda = { ...cuda, phase: 'downloading', received: 100000000 };
                await route.fulfill({ status: 202 });
                return;
            }
            const response = await route.fetch();
            const data = await response.json();
            await route.fulfill({ response, json: { ...data, cuda } });
        });
        const waitCuda = value => page.waitForFunction(text => document.querySelector('#voiceCudaStatus').textContent.includes(text), value);
        await waitCuda('无法使用 CUDA 13');
        assert.equal(await cudaButton.count(), 0, 'incompatible drivers do not offer a large download');
        cuda = { ...cuda, availability: 'missing' };
        await cudaButton.click();
        await cudaProgress.waitFor({ state: 'visible' });
        assert.equal(await cudaProgress.getAttribute('value'), '100000000');
        assert.equal(downloadClicks, 1);
        cuda = { ...cuda, phase: null, error: 'CUDA 下载校验失败，请重新下载。' };
        await waitCuda('校验失败');
        await cudaButton.waitFor({ state: 'visible' });
        cuda = { ...cuda, availability: 'ready', error: null };
        await waitCuda('CUDA 组件已就绪');
        assert.equal(await cudaButton.count(), 0, 'verified CUDA libraries need no download');
        await page.locator('#voiceBackend').selectOption('vulkan');
        await page.locator('#voiceCuda').waitFor({ state: 'hidden' });
        await page.route('**/api/voice/microphones', route => route.fulfill({json:[{id:'fixture-mic',name:'测试麦克风'}]}));
        let releaseCleanup;
        const cleanup = new Promise(resolve => { releaseCleanup=resolve; });
        await page.route(/\/api\/hotkey\/recording(?:\/[a-z]+)?$/, async route => {
            const id='a'.repeat(32);
            if(route.request().method()==='DELETE') await cleanup;
            return route.fulfill({json:route.request().method()==='GET' ? {id,state:'completed',binding:'F6',label:'F6'} : {id}});
        });
        await page.locator('nav [data-page="outgoing"]').click();
        await page.locator('nav [data-section="voiceInputCard"]').click();
        await page.waitForFunction(() => document.getElementById('autoSaveStatus').textContent === '所有更改已保存');
        const microphone = page.locator('#voiceInputDevice');
        await microphone.selectOption('fixture-mic');
        const voiceInput = page.locator('#voiceInputEnabled');
        assert.equal(await voiceInput.isChecked(),false);
        await page.locator('#voiceInputLanguage').selectOption('en');
        const enabledSave = page.waitForResponse(response => response.url().endsWith('/api/config') &&
            response.request().method() === 'PUT' && response.request().postDataJSON().voiceInputEnabled &&
            response.request().postDataJSON().voiceInputLanguage==='en');
        await voiceInput.check();
        const enabledResponse=await enabledSave;
        assert.equal(enabledResponse.ok(),true,'enabling dictation before binding a key can be saved');
        const enabledConfig=(await enabledResponse.json()).settings;
        assert.equal(enabledConfig.voiceInputTeamKey,'');
        assert.equal(enabledConfig.voiceInputDevice,'fixture-mic');
        assert.equal(enabledConfig.voiceInputLanguage,'en');
        await page.waitForSelector('#voiceInputSetup:visible');
        await page.waitForFunction(() => document.getElementById('autoSaveStatus').textContent === '所有更改已保存');

        let releaseWrite, captureWrite;
        const heldWrite=new Promise(resolve=>{releaseWrite=resolve;});
        const olderWrite=new Promise(resolve=>{captureWrite=resolve;});
        const holdPriorSave=async route=>{
            const request=route.request();
            if(request.method()!=='PUT'||request.postDataJSON().voiceInputTeamKey) return route.continue();
            const response=await route.fetch();
            captureWrite();
            await heldWrite;
            await route.fulfill({response});
        };
        await page.route('**/api/config',holdPriorSave);
        let savedInput;
        try {
            await page.locator('#voiceInputLanguage').selectOption('ja');
            await Promise.race([olderWrite,delay(6000).then(()=>assert.fail('earlier auto-save was not captured'))]);
            await page.locator('[data-record-voice="voiceInputTeamKey"]').click();
            await page.waitForFunction(() => document.getElementById('voiceInputTeamKey').value === 'F6');
            const inputSave = page.waitForResponse(response => response.url().endsWith('/api/config') &&
                response.request().method() === 'PUT' && response.request().postDataJSON().voiceInputTeamKey==='F6',{timeout:6000});
            releaseWrite();
            savedInput=await inputSave;
        } finally {
            releaseWrite();
            releaseCleanup();
            await page.unroute('**/api/config',holdPriorSave);
        }
        assert.equal(savedInput.ok(),true);
        const inputConfig = (await savedInput.json()).settings;
        assert.equal(inputConfig.voiceInputTeamKey,'F6');
        assert.equal(inputConfig.voiceInputDevice,'fixture-mic');
        assert.equal(inputConfig.voiceInputLanguage,'ja');
        await page.waitForSelector('#voiceInputSetup',{state:'hidden'});
        await page.reload();
        await page.locator('nav [data-page="outgoing"]').click();
        await page.locator('nav [data-section="voiceInputCard"]').click();
        await page.waitForFunction(() => document.getElementById('voiceInputEnabled').checked);
        assert.equal(await page.locator('#voiceInputTeamKey').inputValue(),'F6');
        assert.equal(await microphone.inputValue(),'fixture-mic');
        assert.equal(await page.locator('#voiceInputLanguage').inputValue(),'ja');
        assert.equal((await saved()).enabled,false);
        assert.deepEqual(errors, []);
        console.log('PASS voice settings persist before binding and keep newly recorded hotkeys while an earlier save finishes.');
    } finally {
        if (page) await page.evaluate(() => fetch('/api/stop', { method: 'POST', headers: { 'X-DLT-Request':'1' } })).catch(() => {});
        if (browser) await browser.close();
        if (child.exitCode === null) child.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
