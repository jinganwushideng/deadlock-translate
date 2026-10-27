const {chromium}=require('playwright'),http=require('node:http'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),version=JSON.parse(fs.readFileSync(path.join(root,'version.json'),'utf8')).version;
let passed=0,record=null,starts=0,cancels=0,configSaves=0,savedConfig,failBegin=false,lastMode;
let settings={enabled:true,outgoingEnabled:true,outgoingHotkey:'Ctrl+Enter',incomingScope:'all_players',mode:'hybrid',fastProvider:'bing',bilingualNames:true,fontSize:17,scanIntervalMs:200,llmConcurrency:4,fastConcurrency:4,outgoingConcurrency:3,outgoingQueueLimit:12,outgoingTimeoutSeconds:30,fastTimeoutSeconds:5,llmTimeoutSeconds:30,llmBaseUrl:'https://fixture.invalid/v1',model:'fixture',azureRegion:'',outgoingMode:'auto',outgoingDisplayMode:'translation',outgoingFallbackFast:false,reserveSendSlot:true,transport:'fragment',chineseNameStyle:'current'};
const diagnostics={version,clientVersion:version,phase:'connected',backendElevated:false,certificate:{state:'ready',message:'有效证书'},gamePrivileges:{detected:true,elevated:false,sameUser:true},sending:{state:'idle'},hotkey:{available:true,label:'Ctrl + Enter',triggered:0,acknowledged:0,ackTimeouts:0}};
const server=http.createServer(async(req,res)=>{
 const url=req.url.split('?')[0];if(url.startsWith('/api/')){
  res.setHeader('Content-Type','application/json');let body='';for await(const c of req)body+=c;
  if(url==='/api/config'){if(req.method==='PUT'){configSaves++;settings=JSON.parse(body);savedConfig=settings;}res.end(JSON.stringify({settings,hasLlmKey:false,hasAzureKey:false}));}
  else if(url==='/api/hotkey/recording'){
   starts++;lastMode=JSON.parse(body).inGame;if(failBegin){res.statusCode=409;res.end(JSON.stringify({error:'前台窗口是管理员权限，后台是普通权限。可使用按键选项。'}));return;}
   record={id:starts.toString(16).padStart(32,'0'),state:'arming',secondsRemaining:15};res.end(JSON.stringify(record));
  }else if(url.startsWith('/api/hotkey/recording/')){
   const id=url.split('/').pop();if(!record||record.id!==id){res.statusCode=404;res.end(JSON.stringify({error:'录制已结束'}));}
   else if(req.method==='DELETE'){cancels++;record={...record,state:'cancelled'};res.end('{}');}else res.end(JSON.stringify(record));
  }else if(url==='/api/status')res.end(JSON.stringify({engine:{messages:0,pending:0,connected:true},terms:0,sending:{pending:0},diagnostics}));
  else if(url==='/api/outgoing')res.end(JSON.stringify({items:[],hotkey:diagnostics.hotkey,hotkeyAvailable:true}));
  else if(url==='/api/terms')res.end('[]');else res.end('{}');return;
 }
 const name=url==='/style.css'?'style.css':url==='/app.js'?'app.js':'index.html';res.setHeader('Content-Type',name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html');res.end(fs.readFileSync(path.join(root,'src/Bridge/wwwroot',name)));
});
function check(value,name){assert(value,name);passed++;console.log('PASS '+name);}
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({channel:'msedge',headless:true}),page=await browser.newPage({viewport:{width:1100,height:850}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
  async function open(url){await page.goto(url);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:')||document.querySelector('#connection').textContent==='后台已连接');await page.locator('[data-page="shortcuts"]').click();}
  await open(base+'/in-game/fixture');check(await page.locator('#chordCtrl').inputValue()==='Ctrl'&&await page.locator('#chordKey').inputValue()==='Enter','saved binding populates explicit selectors');
  await page.locator('#recordHotkey').click();check(lastMode===true&&record.state==='arming','in-game recording requests native arming');record={...record,state:'recording'};await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('请按组合键'));
  await page.evaluate(()=>document.dispatchEvent(new KeyboardEvent('keydown',{key:'w',code:'F11',altKey:true,bubbles:true})));
  check(await page.locator('#outgoingHotkey').inputValue()==='Ctrl+Enter','wrong Panorama DOM code F11 cannot change the binding');
  record={...record,state:'completed',binding:'LAlt+W',label:'左 Alt＋W'};await page.waitForFunction(()=>document.querySelector('#outgoingHotkey').value==='LAlt+W');
  check(await page.locator('#chordAlt').inputValue()==='LAlt'&&await page.locator('#chordKey').inputValue()==='W','native result writes W and exact Alt side');
  check(configSaves===0,'recording changes a draft without saving settings');check(await page.locator('#recordHotkey').innerText()==='录制快捷键','recording restores its button');
  await page.locator('#save').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('设置已保存'));check(savedConfig.outgoingHotkey==='LAlt+W','explicit save persists the correct native chord');
  await open(base+'/');await page.locator('#recordHotkey').click();check(lastMode===false,'browser recording uses the same native route with browser scope');record={...record,state:'completed',binding:'LAlt+Space',label:'左 Alt＋空格'};
  await page.waitForFunction(()=>document.querySelector('#outgoingHotkey').value==='LAlt+Space');check(true,'browser Alt+Space does not rely on DOM keyboard delivery');
  await page.locator('#chordAlt').selectOption('Alt');await page.locator('#chordCtrl').selectOption('');await page.locator('#chordKey').selectOption('W');check(await page.locator('#outgoingHotkey').inputValue()==='Alt+W','manual Alt+W selection works without any key event');
  await page.locator('#chordCtrl').selectOption('RCtrl');await page.locator('#chordShift').selectOption('LShift');check(await page.locator('#outgoingHotkey').inputValue()==='RCtrl+Alt+LShift+W','manual selections preserve side and canonical modifier order');
  await page.locator('#resetHotkey').click();check(await page.locator('#chordCtrl').inputValue()===''&&await page.locator('#chordAlt').inputValue()==='LAlt'&&await page.locator('#chordKey').inputValue()==='Space','default reset also resets selectors');
  await page.locator('#recordHotkey').click();record={...record,state:'recording'};await page.locator('#recordHotkey').click();check(record.state==='cancelled','cancel button cancels native recording');
  await page.locator('#recordHotkey').click();record={...record,state:'expired',error:'录制超时或页面已关闭'};await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('录制超时'));check(await page.locator('#outgoingHotkey').inputValue()==='LAlt+Space','timeout retains the prior draft');
  await page.locator('#recordHotkey').click();await page.locator('[data-page="appearance"]').click();await page.waitForFunction(()=>document.querySelector('#recordHotkey').textContent==='录制快捷键');check(record.state==='cancelled','changing tabs cancels active capture');
  await page.locator('[data-page="shortcuts"]').click();failBegin=true;await page.locator('#recordHotkey').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('管理员权限'));check(await page.locator('#chordAlt').isEnabled(),'permission mismatch provides selectors as a usable fallback');
  await page.locator('#chordAlt').selectOption('Alt');await page.locator('#chordKey').selectOption('W');check(await page.locator('#outgoingHotkey').inputValue()==='Alt+W','selectors remain functional when the native hook is unavailable');
  await page.locator('#outgoingHotkey').fill('RAlt+F11');check(await page.locator('#chordAlt').inputValue()==='RAlt'&&await page.locator('#chordKey').inputValue()==='F11','manual text and selectors stay synchronized');
  await page.setViewportSize({width:600,height:760});await page.screenshot({path:path.join(root,'artifacts/native-recording-narrow.png'),fullPage:true});check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'controls fit a narrow in-game/browser window');
  await page.setViewportSize({width:1100,height:850});await page.screenshot({path:path.join(root,'artifacts/native-recording-desktop.png'),fullPage:true});check(errors.length===0,'recording UI has no browser errors');
  check(!fs.readFileSync(path.join(root,'src/Settings/app.ts'),'utf8').includes('recordedModifiers'),'old DOM modifier/keycode recorder is removed');
  fs.writeFileSync(path.join(root,'artifacts/native-recording-ui-tests.json'),JSON.stringify({passed,starts,cancels,scope:'mock native HTTP and headless browser; no real hook, game keyboard input or cloud calls'},null,2));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(()=>server.close());
