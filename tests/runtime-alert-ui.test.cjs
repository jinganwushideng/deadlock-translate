const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'))).version;
let passed=0,adminCalls=0,exportCount=0,unauthorized=false,status;
const check=(v,n)=>{assert(v,n);passed++;console.log('PASS '+n);};
const settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'LAlt+Space',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',outgoingDisplayMode:'translation',outgoingMode:'auto',fontSize:16,scanIntervalMs:300,fastConcurrency:4,llmConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://api.example.com/v1',model:'test',transport:'fragment'};
function healthy(){return {engine:{messages:0,pending:0,connected:true},terms:1,sending:{pending:0},diagnostics:{version,clientVersion:version,backendElevated:false,phase:'connected',certificate:{state:'ready',message:'有效证书',daysRemaining:100},gamePrivileges:{detected:true,elevated:false,sameUser:true},sending:{state:'idle'},hotkey:{available:true,label:'左 Alt + 空格',triggered:0,acknowledged:0,ackTimeouts:0,backendElevated:false,gameForeground:false,gameElevated:null,gameSameUser:null,confirmationTimedOut:false}}};}
status=healthy();
const server=http.createServer((req,res)=>{
 const url=req.url.split('?')[0];res.setHeader('Content-Type','application/json');
 if(url==='/api/status'||url==='/api/diagnostics'){if(unauthorized){res.statusCode=401;res.end('{}');}else res.end(JSON.stringify(url.endsWith('diagnostics')?status.diagnostics:status));}
 else if(url==='/api/config')res.end(JSON.stringify({settings,hasAzureKey:false,hasLlmKey:false}));
 else if(url==='/api/terms')res.end('[]');
 else if(url==='/api/outgoing')res.end(JSON.stringify({items:[],hotkey:status.diagnostics.hotkey,hotkeyAvailable:true}));
 else if(url==='/api/admin-restart'){adminCalls++;res.end('{}');}
 else if(url==='/api/launch-options')res.end(JSON.stringify({admin:'"D:\\Apps\\DeadlockTranslate\\DeadlockTranslate.exe" --admin-compatible --steam-game %command%'}));
 else if(url==='/api/diagnostics/export'){exportCount++;res.end(JSON.stringify({file:'fixture-log.zip'}));}
 else if(url==='/app.js'||url==='/style.css'){res.setHeader('Content-Type',url.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',url.slice(1))));}
 else{res.setHeader('Content-Type','text/html; charset=utf-8');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot/index.html')));}
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1120,height:850}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/in-game/test');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  check(await page.locator('#runtimeAlerts').isHidden(),'healthy settings do not show a problem banner');
  status.diagnostics.gamePrivileges={detected:true,elevated:true,sameUser:true};
  await page.waitForSelector('[data-issue="privilege"]');
  check((await page.locator('[data-issue="privilege"]').innerText()).includes('原 Windows 用户'),'permission mismatch explains the correct UAC user');
  check(adminCalls===0,'detecting a mismatch never requests automatic elevation');
  check(await page.locator('[data-issue="privilege"] [data-help-action="admin"]').isVisible(),'verified same-user mismatch offers opt-in admin restart');
  await page.locator('[data-page="providers"]').click();check(await page.locator('[data-issue="privilege"]').isVisible(),'warning remains visible across settings tabs');
  await page.evaluate(()=>{window.savedAction=document.querySelector('[data-issue="privilege"] [data-help-action="admin"]');window.savedAction.focus();});
  await page.waitForTimeout(1300);check(await page.evaluate(()=>window.savedAction===document.querySelector('[data-issue="privilege"] [data-help-action="admin"]')&&document.activeElement===window.savedAction),'unchanged poll preserves focused warning actions');
  await page.locator('[data-issue="privilege"] [data-help-action="copy-admin"]').click();await page.waitForFunction(()=>document.querySelector('#adminLaunchCommand').value.includes('--admin-compatible'));check((await page.locator('#adminLaunchCommand').inputValue()).includes('--admin-compatible --steam-game %command%'),'copy action uses the correct flag order');
  await page.locator('[data-issue="privilege"] [data-help-action="admin"]').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('等待你确认'));check(adminCalls===1,'elevation request only occurs after the explicit action');
  status.diagnostics.gamePrivileges.sameUser=false;await page.waitForSelector('[data-issue="different-user"]');check(await page.locator('[data-issue="different-user"] [data-help-action="admin"]').count()===0,'cross-user problem never offers an elevation shortcut');
  status=healthy();status.diagnostics.gamePrivileges={detected:true,elevated:null,sameUser:null};status.diagnostics.phase='game-stale';await page.waitForSelector('[data-issue="unknown-permission"]');check(await page.locator('[data-issue="privilege"]').count()===0,'unknown token state is not declared an administrator mismatch');
  status=healthy();status.engine.connected=false;status.diagnostics.phase='game-stale';await page.waitForSelector('[data-issue="bridge"] [data-help-action="reconnect"]');
  check((await page.locator('[data-issue="bridge"]').innerText()).includes('后台正常'),'stale game channel is not reported as backend exit');
  await page.locator('[data-issue="bridge"] [data-help-action="reconnect"]').click();await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:reconnect:'));
  check((await page.locator('#notice').textContent()).includes('是否恢复以游戏连接状态为准'),'reconnect action reports a request instead of claiming recovery');
  await page.waitForTimeout(500);check((await page.title()).startsWith('DLTSETTINGS:reconnect:'),'native recovery title is held long enough for the game poll');
  check(adminCalls===1,'reconnect action does not restart or elevate the backend');
  await page.goto(base+'/settings/test');await page.waitForSelector('[data-issue="bridge"]');check(await page.locator('[data-help-action="reconnect"]').count()===0,'ordinary browser does not pretend it can control the native game bridge');
  status=healthy();await page.goto(base+'/in-game/test');await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));
  status=healthy();status.diagnostics.certificate.state='untrusted';await page.waitForSelector('[data-issue="certificate"]');check((await page.locator('[data-issue="certificate"]').innerText()).includes('完整退出并重启 Steam'),'untrusted certificate gives the Steam restart instruction');
  status=healthy();status.diagnostics.clientVersion='0.0.1';await page.waitForSelector('[data-issue="version"]');check((await page.locator('[data-issue="version"]').innerText()).includes('同时更新后台和游戏 VPK'),'version mismatch requires a complete update');
  for(const [code,message] of [['ReasoningBudget','思考耗尽'],['OutputTruncated','被截断'],['NameMismatch','未保留游戏专名']]){status=healthy();status.diagnostics.sending={state:'failed',failureCode:code};await page.waitForFunction(msg=>document.querySelector('[data-issue="sending"]')?.textContent.includes(msg),message);check((await page.locator('[data-issue="sending"]').innerText()).includes(message),'sending output failure is specific: '+code);}
  status=healthy();status.diagnostics.sending={state:'failed',failureCode:'Unauthorized'};await page.waitForFunction(()=>document.querySelector('[data-issue="sending"]')?.textContent.includes('不同的问题'));check((await page.locator('[data-issue="sending"]').innerText()).includes('不同的问题'),'provider authentication is distinguished from Windows privilege');
  status.diagnostics.sending={state:'failed',failureCode:'NativeInputTruncated',refusalCode:'input-truncated'};await page.waitForFunction(()=>document.querySelector('[data-issue="sending"]').textContent.includes('游戏输入框截短'));
  check((await page.locator('[data-issue="sending"]').innerText()).includes('80 个 Unicode')&&!(await page.locator('[data-issue="sending"]').innerText()).includes('提高模型输出上限'),'native truncation has a split-message remedy rather than model-token advice');
  status.diagnostics.sending={state:'waiting_channel'};await page.waitForFunction(()=>document.querySelector('[data-issue="sending"]').textContent.includes('发送频道未确认'));check((await page.locator('[data-issue="sending"]').innerText()).includes('不会改发全体'),'channel warning does not suggest changing to global chat');
  for(const [networkError,title] of [['NameResolutionError','无法解析 API 域名'],['SecureConnectionError','HTTPS 连接验证失败'],['ProxyTunnelError','代理隧道连接失败'],['ResponseEnded','API 响应传输中断']]){
   status=healthy();status.diagnostics.sending={state:'failed',failureCode:'Network',networkError};await page.waitForFunction(expected=>document.querySelector('[data-issue="sending"]')?.textContent.includes(expected),title);check((await page.locator('[data-issue="sending"]').innerText()).includes(title),'network category has specific recovery guidance: '+networkError);
  }
  for(const [refusalCode,title] of [['native-close','聊天框没有正常收起'],['submit-unconfirmed','无法确认游戏是否读取']]){
   status=healthy();status.diagnostics.sending={state:'failed',failureCode:'Other',refusalCode};await page.waitForFunction(expected=>document.querySelector('[data-issue="sending"]')?.textContent.includes(expected),title);check((await page.locator('[data-issue="sending"]').innerText()).includes(title),'native send failure has explicit recovery guidance: '+refusalCode);
  }
  status=healthy();status.diagnostics.hotkey.confirmationTimedOut=true;await page.waitForSelector('[data-issue="ack"]');check(await page.locator('[data-issue="ack"] [data-help-action="shortcuts"]').isVisible(),'unconfirmed hotkey leads to shortcut settings');
  status=healthy();status.diagnostics.hotkey.available=false;await page.waitForSelector('[data-issue="hook"]');check((await page.locator('[data-issue="hook"]').innerText()).includes('重建监听'),'hook failure gives a reinitialization instruction');
  status=healthy();status.diagnostics.hotkey.ackTimeouts=50;status.diagnostics.hotkey.triggered=51;status.diagnostics.hotkey.acknowledged=51;await page.waitForFunction(()=>document.querySelector('#runtimeAlerts').hidden);check(true,'resolved historical timeouts do not leave a stale warning');
  status=healthy();status.diagnostics.gamePrivileges={detected:false,elevated:null,sameUser:null};status.diagnostics.phase='waiting-for-game';await page.waitForTimeout(1100);check(await page.locator('#runtimeAlerts').isHidden(),'idle backend without a game is not treated as a connection fault');
  status=healthy();status.diagnostics.gamePrivileges.elevated=true;await page.waitForSelector('[data-issue="privilege"]');await page.setViewportSize({width:600,height:760});await page.screenshot({path:path.join(root,'artifacts/runtime-alert-mobile.png'),fullPage:true});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'warning fits a narrow settings window');
  await page.setViewportSize({width:1120,height:850});await page.screenshot({path:path.join(root,'artifacts/runtime-alert-desktop.png'),fullPage:true});
  status=healthy();status.diagnostics.providers=[{provider:'bing',stage:'bing-page',httpStatus:302,code:'ServiceRedirect'}];await page.waitForSelector('[data-issue="provider-bing"]');check((await page.locator('[data-issue="provider-bing"]').innerText()).includes('获取网页凭据时'),'Bing redirect banner identifies the page-token request stage');
  status.diagnostics.providers=[{provider:'bing',stage:'bing-translate',httpStatus:200,code:'InvalidData'}];await page.waitForFunction(()=>document.querySelector('[data-issue="provider-bing"]').textContent.includes('没有返回有效翻译内容'));check(true,'HTTP 200 verification content has a protocol warning rather than a successful-service claim');
  status.diagnostics.providers=[{provider:'llm',stage:'llm-translate',httpStatus:302,code:'ServiceRedirect'}];await page.waitForSelector('[data-issue="provider-llm"]');check((await page.locator('[data-issue="provider-llm"]').innerText()).includes('没有把密钥转交'),'LLM redirect explanation preserves credential safety');
  status=healthy();await page.waitForFunction(()=>document.querySelector('#runtimeAlerts').hidden);check(true,'recovered provider status removes its warning');
  status=healthy();status.diagnostics.clientVersion='<img src=x onerror="window.injected=true">';await page.waitForSelector('[data-issue="version"]');check(await page.locator('#runtimeAlerts img').count()===0&&!await page.evaluate(()=>window.injected),'diagnostic strings are rendered only as plain text');
  unauthorized=true;await page.waitForSelector('[data-issue="session"]');check(await page.locator('[data-issue="offline"]').count()===0,'expired settings session is not described as a stopped backend');
  unauthorized=false;status=healthy();await page.waitForFunction(()=>document.querySelector('#runtimeAlerts').hidden);check(true,'warning disappears automatically after recovery');
  await page.route('**/api/status',r=>r.abort('failed'));await page.waitForSelector('[data-issue="offline"]');check((await page.locator('[data-issue="offline"]').innerText()).includes('无法确认是不是管理员权限问题'),'offline fallback does not guess a privilege cause');
  check(errors.length===0,'banner and action handlers have no browser errors');
  fs.writeFileSync(path.join(root,'artifacts/runtime-alert-ui-tests.json'),JSON.stringify({passed,realApi:false,realUac:false,realGameInput:false},null,2));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
