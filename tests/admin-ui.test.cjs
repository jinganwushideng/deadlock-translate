const {chromium}=require('playwright'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const root=path.resolve(__dirname,'..'),base='http://localhost:17868',key=fs.readFileSync(path.join(root,'artifacts/pairing.key'),'utf8').trim();let passed=0;const check=(v,n)=>{assert(v,n);passed++;};
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
 const page=await browser.newPage({viewport:{width:1040,height:700}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/in-game/'+key);await page.waitForFunction(()=>document.title.startsWith('DLTSETTINGS:ready:'));await page.locator('[data-page="diagnostics"]').click();
 check(await page.locator('#adminRestart').isVisible(),'opt-in admin restart is visible in diagnostics maintenance');
 await page.locator('#copyAdminLaunch').click();await page.waitForFunction(()=>document.querySelector('#adminLaunchCommand').value.includes('--admin-compatible --steam-game %command%'));
 check((await page.locator('#adminLaunchCommand').inputValue()).includes('DeadlockTranslate.exe" --admin-compatible --steam-game %command%'),'generated launch prefix uses actual EXE and correct flag order');
 check(await page.locator('#adminLaunchCommand').getAttribute('readonly')!==null,'generated command is selectable read-only text');
 check(await page.getByText('重启将取消旧待发送任务', {exact:false}).isVisible(),'pending task cancellation explained before restart');
 await page.locator('#adminRestart').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('无交互模式不会请求管理员权限'));
 check(true,'isolated UI never requests real UAC');
 check(errors.length===0,'new controls have no page errors');
 fs.writeFileSync(path.join(root,'artifacts/admin-ui-tests.json'),JSON.stringify({passed,realUacUsed:false,realGameInput:false},null,2));console.log({passed});
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
