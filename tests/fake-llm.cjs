// Local fake Chat Completions service for tests. It never reaches the network and never needs a
// real provider key, so the outgoing send path and the settings page can be verified without
// spending API quota. Not shipped in the package.
const http = require('node:http');
const port = Number(process.env.DLT_FAKE_LLM_PORT || 17870);
const english = 'Wait for my ultimate, don\'t attack.';
const chinese = '别打，等我大招。';
let calls = 0;
const server = http.createServer((req, res) => {
    if (req.method !== 'POST' || !req.url.startsWith('/v1/chat/completions')) { res.statusCode = 404; res.end('{}'); return; }
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
        calls++;
        const wantingEnglish = body.includes('natural English');
        console.log('fake-llm request ' + calls + ' -> ' + (wantingEnglish ? 'en' : 'zh'));
        // The service must never treat message content as instructions for itself.
        const content = wantingEnglish ? english : chinese;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ id: 'fake-' + calls, choices: [{ index: 0, finish_reason: 'stop', message: { role: 'assistant', content } }] }));
    });
});
server.listen(port, '127.0.0.1', () => console.log('fake-llm listening on http://127.0.0.1:' + port));
process.on('SIGTERM', () => server.close(() => process.exit(0)));
