// Execute the actual HTML bridge and Panorama client together under bounded
// native title/URL surfaces. No translation provider or game process is used.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { fixture } = require('./build-translation-action.test.cjs');
const html = fs.readFileSync(path.join(__dirname, '../src/Bridge/BuildTextBridge.cs'), 'utf8');
const script = html.slice(html.indexOf('<script>') + 8, html.indexOf('</script>'));
const limit = 4096;

async function exercise(captured, staleProperty = false) {
    const f = fixture(false), events = {}, calls = [], titles = [], urls = [];
    const values = captured ? captured.plan.texts : ['Build title', ...Array.from({ length: 45 }, (_, i) =>
        'Field ' + i + ': ' + 'Long note with quotes " and 中文🙂\n'.repeat(12))];
    const cached = captured ? captured.cache : { translations: values.map((_, i) => i < 31 ? '译文🙂 ' + i : null), cacheHits: 31 };
    let nativeTitle = '';
    const document = {};
    Object.defineProperty(document, 'title', {
        get: () => nativeTitle,
        set: value => { titles.push(value.length); nativeTitle = value.slice(0, limit); f.bridge.title = staleProperty ? 'DLTBUILD:{"id":-1}' : nativeTitle; }
    });
    const location = { pathname: '/build-text/fixture', hash: '' };
    const scope = { document, location, AbortController, addEventListener: (name, fn) => events[name] = fn,
        fetch: async (_, options) => {
            const input = JSON.parse(options.body); calls.push(input);
            let result;
            if (input.loadPlan) result = { planStatus: 'ready', plan: { id: 101, hero: 17, version: 1, name: 'Build title', texts: values } };
            else if (input.cacheOnly) {
                result = { translations: input.texts.map(text => cached.translations[values.indexOf(text)]),
                    cacheHits: input.texts.filter(text => cached.translations[values.indexOf(text)] !== null).length,
                    // Keep the old redundant snapshot deliberately: framing must
                    // handle the real failing response, even before its removal.
                    snapshot: captured ? captured.cache.snapshot : Object.fromEntries(values.map(text => [text, { value: '缓存 ' + text, refine: false }])) };
            } else result = { translations: input.texts.map(text => '新译 ' + text), translated: input.texts.length };
            return { ok: true, json: async () => ({ id: input.id, revision: 'r1', ...result }) };
        }
    };
    vm.runInNewContext(script, scope);
    f.bridge.GetTitle = () => nativeTitle;
    f.bridge.SetURL = url => {
        urls.push(url.length);
        if (url === 'about:blank') { events.pagehide(); return; }
        assert.ok(url.length < limit, 'no native URL can be truncated');
        location.hash = url.slice(url.indexOf('#')); events.hashchange();
    };
    f.click();
    for (let i = 0; i < 6000; i++) {
        await Promise.resolve(); await Promise.resolve();
        f.frame(50);
        const job = f.scene.__dltBuildText.job;
        if (job && job.total === values.length && job.done === job.total && !job.pending && !job.queue.length) break;
    }
    const state = f.scene.__dltBuildText, job = state.job;
    assert.equal(job.done, values.length);
    assert.equal(job.failed, 0);
    assert.equal(state.decodeErrors, 0);
    assert.equal(state.timeouts, 0);
    assert.ok(Math.max(...titles) < 1600);
    assert.ok(state.receivedParts > 1);
    assert.ok(calls.filter(input => input.cacheOnly).every(input => input.context === '' && input.includeSnapshot === false));
    const cloud = calls.filter(input => !input.cacheOnly && !input.loadPlan);
    assert.ok(cloud.length > 0);
    assert.equal(new Set(cloud.map(input => JSON.stringify(input.texts))).size, cloud.length, 'reading chunks cannot repeat provider requests');
    assert.equal(cloud.flatMap(input => input.texts).length, values.length - 31);
    assert.ok(cloud.every(input => input.context.length <= 6000));
    assert.equal(job.cacheHits, 31);
    console.log('PASS ' + (captured ? 'actual 46-field stalled response' : 'large bilingual/emoji plan and annotations') +
        (staleProperty ? ' with stale title property' : '') + ': full result, bounded frames, 31 cached, only 15 newly translated');
}

async function main() {
    await exercise(null);
    await exercise(null, true);
    if (process.argv[2]) {
        const dir = process.argv[2];
        const plan = JSON.parse(fs.readFileSync(path.join(dir, 'local-response-private.json'), 'utf8')).plan;
        const cache = JSON.parse(fs.readFileSync(path.join(dir, 'cache-response-private.json'), 'utf8'));
        assert.ok(('DLTBUILD:' + JSON.stringify(cache)).length > limit, 'original result reproduces truncation in bounded native title');
        assert.throws(() => JSON.parse(('DLTBUILD:' + JSON.stringify(cache)).slice(0, limit).slice(9)));
        await exercise({ plan, cache });
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
