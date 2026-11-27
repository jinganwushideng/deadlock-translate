const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const {randomBytes} = require('node:crypto');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const {liteVersion: version} = require('../version.json');

const runtime = process.env.DLT_TEST_RUNTIME || path.resolve(__dirname, '../dist/DeadlockTranslate-Lite-' + version);
const state = fs.mkdtempSync(path.join(os.tmpdir(), 'dlt-lite-settings-'));
const pairing = randomBytes(24).toString('hex');
const pairingFile = path.join(state,'pairing.key');
fs.writeFileSync(pairingFile,pairing);
const terms = [{source:'尖尖头',target:'大和',english:'Yamato',kind:'name',ambiguous:false}];
const original = {incomingTargetLanguage:'ja', outgoingTargetLanguage:'custom', outgoingCustomLanguage:'荷兰语',
    outgoingCustomLanguageCode:'nl', outgoingHotkey:'F8', mode:'quality', outgoingMode:'quality',
    incomingDisplayMode:'independent', voiceInputEnabled:true, buildTextAutoEnabled:true};
for (const [name,value] of [['settings',original],['terms',terms]])
    fs.writeFileSync(path.join(state,name+'.json'),JSON.stringify(value));
const delay = ms => new Promise(resolve => setTimeout(resolve,ms));
const port = () => new Promise(resolve => {
    const listener = net.createServer();
    listener.listen(0,'127.0.0.1',() => { const value = listener.address().port; listener.close(() => resolve(value)); });
});

(async () => {
    const http = await port(), https = await port();
    const base = 'http://localhost:' + http;
    const child = spawn(path.join(runtime,'DeadlockTranslate.exe'),
        ['--no-shell','--no-hotkey','--data-dir',state,'--pairing-file',pairingFile,'--port',String(http),'--https-port',String(https)],
        {cwd:runtime,windowsHide:true,stdio:'ignore'});
    const key = pairing;
    let browser;
    try {
        for (let i=0;;i++) {
            try { if((await fetch(base+'/health')).ok) break; } catch {}
            assert(i<100 && child.exitCode===null,'Lite backend starts');
            await delay(100);
        }
        browser = await chromium.launch({channel:'msedge',headless:true});
        const page = await browser.newPage({viewport:{width:1280,height:900}});
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(base+'/in-game/'+key+'?windowProtocol=2&windowLoad=1');
        await page.waitForFunction(() => document.title.startsWith('DLTSETTINGS:ready:'));
        assert.equal(await page.locator('[data-lite-page]').count(),4);
        assert.equal(await page.locator('#mode').inputValue(),'fast');
        assert.equal(await page.locator('#incomingTargetLanguage').inputValue(),'ja');
        assert(await page.locator('#providerCard').isHidden());
        await page.locator('#mode').selectOption('quality');
        await page.locator('#providerCard').waitFor({state:'visible'});
        await page.locator('#mode').selectOption('fast');
        await page.locator('[data-lite-page="outgoing"]').click();
        assert.equal(await page.locator('#outgoingMode').inputValue(),'fast');
        assert.equal(await page.locator('#outgoingTargetLanguage').inputValue(),'custom');
        assert.equal(await page.locator('#outgoingCustomLanguageCode').inputValue(),'nl');
        assert.equal(await page.locator('#outgoingHotkey').inputValue(),'F8');
        await page.locator('#outgoingEnabled').uncheck();
        await page.locator('#saveLite').click();
        await page.waitForFunction(() => document.querySelector('#sendKeyStatus').textContent.includes('发送翻译已关闭'));
        await page.locator('#outgoingEnabled').check();
        await page.locator('#sendKey').selectOption('F9');
        await page.locator('#saveLite').click();
        await page.waitForFunction(() => document.querySelector('#sendKeyStatus').textContent === '已保存 F9');
        await page.reload();
        await page.waitForFunction(() => document.title.startsWith('DLTSETTINGS:ready:'));
        await page.locator('[data-lite-page="outgoing"]').click();
        assert.equal(await page.locator('#sendKey').inputValue(),'F9');
        const recordingId = 'a'.repeat(32);
        let recorderReleased = false;
        await page.route(/\/api\/hotkey\/recording(?:\/[a-f0-9]+)?$/, route => {
            const method = route.request().method();
            if (method === 'DELETE') recorderReleased = true;
            return route.fulfill({json:method === 'GET' ? {id:recordingId,state:'completed',binding:'RAlt+Space'} : {id:recordingId}});
        });
        await page.locator('#recordSendKey').click();
        await page.waitForFunction(() => document.querySelector('#sendKeyStatus').textContent === '已保存 RAlt+Space');
        assert(recorderReleased);
        assert.equal(await page.locator('#sendAlt').inputValue(),'RAlt');
        assert.equal(await page.locator('#sendKey').inputValue(),'Space');
        assert.equal(await page.locator('#outgoingDisplayMode').isChecked(),false);
        await page.locator('#outgoingDisplayMode').check();
        const [saved] = await Promise.all([
            page.waitForResponse(response => response.url() === base+'/api/config' && response.request().method() === 'PUT'),
            page.locator('#saveLite').click()
        ]);
        const config = (await saved.json()).settings;
        assert.equal(config.outgoingDisplayMode,'bilingual');
        assert.equal(config.outgoingSplitLong,true);
        assert.equal(config.outgoingHotkey,'RAlt+Space');
        assert.equal(config.outgoingEnabled,true);
        assert.equal(config.outgoingSeparateBilingual,false);
        assert.equal(config.incomingDisplayMode,'native');
        assert.equal(config.voiceInputEnabled,false);
        assert.equal(config.buildTextAutoEnabled,false);
        await page.locator('[data-lite-page="glossary"]').click();
        assert.equal(await page.locator('#termRows input').first().inputValue(),'尖尖头');
        await page.locator('#addTerm').click();
        await page.locator('#inputRecoveryButton').click();
        await page.locator('#directSettingsInput').click();
        const row = page.locator('#termRows tr').first();
        await row.getByRole('textbox',{name:'匹配词',exact:true}).fill('大和哥');
        await row.getByRole('textbox',{name:'中文译名',exact:true}).fill('大和');
        await row.getByRole('textbox',{name:'标准英文名',exact:true}).fill('Yamato');
        await page.locator('#saveTerms').click();
        await page.waitForFunction(() => document.querySelector('#termSaveStatus').textContent.includes('词库已保存'));
        const reply = await page.evaluate(async () => (await (await fetch('/api/test',{
            method:'POST',headers:{'Content-Type':'application/json','X-DLT-Request':'1'},
            body:JSON.stringify({direction:'outgoing',provider:'fast',text:'大和哥'})
        })).json()));
        assert.equal(reply.translation,'Yamato');
        const health = await (await fetch(base+'/health')).json();
        let sequence = 0;
        async function exchange(delta = {}) {
            const response = await fetch(base+'/game/'+key,{
                method:'POST', headers:{'Content-Type':'application/json',Origin:base},
                body:JSON.stringify({session:'lite-send-regression',seq:++sequence,
                    clientVersion:health.version,clientBuildId:health.buildId,multipartSupported:true, ...delta})
            });
            assert(response.ok,await response.clone().text());
            return response.json();
        }
        const queued = await exchange({chatOpen:true,outgoing:[
            {id:'one',text:'大和哥',channel:'team',holdForInput:true,displayMode:'translation'}
        ]});
        assert(!queued.outgoing.some(item => item.state === 'ready'));
        await exchange({chatOpen:false,outgoingStatus:[
            {seq:1,id:'one',part:0,state:'capture_ready',channel:'team',at:0}
        ]});
        let ready;
        for (let i=0; i<20 && !ready; i++) {
            ready=(await exchange()).outgoing.find(item => item.state === 'ready');
            if (!ready) await delay(100);
        }
        assert.equal(ready?.sendText,'Yamato');
        assert.equal(ready?.channel,'team');
        assert.deepEqual(JSON.parse(fs.readFileSync(path.join(state,'settings.json'))),original);
        assert.deepEqual(JSON.parse(fs.readFileSync(path.join(state,'terms.json'))),terms);
        await page.locator('[data-lite-page="outgoing"]').click();
        await page.locator('#outgoingMode').selectOption('quality');
        await page.locator('#llmBaseUrl').fill('https://custom.example.test/v1');
        await page.locator('#model').fill('custom-chat-test');
        await page.locator('#llmKey').fill('fixture-test-only-key');
        const [llmSaved] = await Promise.all([
            page.waitForResponse(response => response.url() === base+'/api/config' && response.request().method() === 'PUT'),
            page.locator('#saveLite').click()
        ]);
        const llm = await llmSaved.json();
        assert.equal(llm.settings.llmPreset,'custom');
        assert.equal(llm.settings.llmCustomBaseUrl,'https://custom.example.test/v1');
        assert.equal(llm.settings.llmCustomModel,'custom-chat-test');
        assert.equal(llm.settings.llmKey,null);
        assert.equal(llm.hasLlmKey,true);
        await page.locator('[data-lite-page="author"]').click();
        assert((await page.locator('#author').innerText()).includes('ともり_一生'));
        assert((await page.locator('#author').innerText()).includes('923443423'));
        assert.equal(await page.locator('#installedLiteVersion').innerText(),version);
        const qr = page.locator('.lite-author-qr');
        await page.waitForFunction(() => document.querySelector('.lite-author-qr').naturalHeight > 0);
        assert(await qr.evaluate(img => Math.abs(img.clientHeight / img.clientWidth - img.naturalHeight / img.naturalWidth) < .025));
        let view = {currentVersion:version,lastChecked:'2026-11-27T00:00:00Z',available:false};
        await page.route('**/api/updates/check',route=>route.fulfill({json:view}));
        await page.locator('#checkUpdates').click();
        assert.equal(await page.locator('#releaseState').innerText(),'GitHub 暂无精简版安装包。');
        assert(await page.locator('#openUpdate').isHidden());
        view = {...view,version:'0.1.10',publishedVersion:'0.1.10',available:true};
        await page.locator('#checkUpdates').click();
        assert.equal(await page.locator('#releaseState').innerText(),'可更新到精简版 0.1.10');
        await page.locator('#openUpdate').waitFor({state:'visible'});
        assert.deepEqual(await page.evaluate(async key => [
            (await fetch('/api/voice')).status, (await fetch('/build-text/'+key)).status
        ],key),[404,404]);
        assert.deepEqual(errors,[]);
        console.log('PASS four settings pages, profile migration, shortcut choice and recording, held native send, live glossary and omitted voice/shop endpoints');
        await page.evaluate(() => fetch('/api/stop',{method:'POST',headers:{'X-DLT-Request':'1'}}));
    } finally {
        if (browser) await browser.close();
        const exited = child.exitCode!==null || child.signalCode!==null;
        if (!exited) {
            const finished = new Promise(resolve => child.once('exit',resolve));
            child.kill();
            await finished;
        }
        const location = fs.realpathSync(state);
        assert.equal(path.dirname(location),fs.realpathSync(os.tmpdir()));
        assert(path.basename(location).startsWith('dlt-lite-settings-'));
        fs.rmSync(location,{recursive:true,force:true,maxRetries:4,retryDelay:100});
    }
})().catch(error => {console.error(error);process.exitCode=1;});
