using DeadlockTranslate;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var initialArgs=args;
var steamMarker=Array.IndexOf(initialArgs,"--steam-game");
var loggingArgs=steamMarker>=0?initialArgs[..steamMarker]:initialArgs;
var dirIndex=Array.IndexOf(loggingArgs,"--data-dir");
var dataDir=dirIndex>=0&&dirIndex+1<loggingArgs.Length?loggingArgs[dirIndex+1]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate");
var adminCompatible=loggingArgs.Contains("--admin-compatible");
var ownerIndex=Array.IndexOf(loggingArgs,"--elevation-owner");
var noShell=loggingArgs.Contains("--no-shell");
string owner;
try {
    owner=StartupCompatibility.Owner();
    if(ownerIndex>=0&&ownerIndex+1>=loggingArgs.Length)throw new StartupFault("identity","管理员兼容启动参数不完整。");
    StartupCompatibility.ValidateOwner(ownerIndex>=0?loggingArgs[ownerIndex+1]:null,owner);
    if(StartupCompatibility.RequiresElevation(adminCompatible,StartupCompatibility.Elevated())){
        if(noShell)throw new StartupFault("elevation-required","管理员兼容模式需要用户确认 UAC；无交互模式不会自动请求权限。");
        using var elevated=Process.Start(StartupCompatibility.ElevationInfo(Environment.ProcessPath!,initialArgs,dataDir,owner));
        if(elevated==null)throw new StartupFault("elevation-failed","管理员兼容启动未完成。");
        return;
    }
} catch(Exception ex){
    // Never write into the original user's directory when UAC switched identities.
    if(ex is not StartupFault {Code:"different-user"}){
        await using var startupLog=new DiagnosticLog(dataDir);
        startupLog.Record("backend",LogKind.StartupFailed,new(Reason:StartupCompatibility.Reason(ex),Stage:"elevation",FailureType:ex.GetType().Name,Win32Error:ex is System.ComponentModel.Win32Exception w?w.NativeErrorCode:null));await startupLog.FlushAsync();
    }
    StartupCompatibility.Notify(ex,noShell);Environment.ExitCode=1;return;
}
await using var audit=new DiagnosticLog(dataDir);
audit.Record("backend",LogKind.Started,new(Version:BuildInfo.Version,BuildId:BuildInfo.Id,Stage:steamMarker>=0?"steam":loggingArgs.Contains("--settings")?"settings":"standalone"));
try {
if(loggingArgs.Contains("--export-diagnostics")){
    var path=await DiagnosticExport.Create(dataDir,audit,new{phase="offline-export"},null);
    if(!loggingArgs.Contains("--no-shell"))try{Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+path+"\""){UseShellExecute=true});}catch{}
    return;
}
var launch=SteamGameLaunch.Split(args);
args=launch.Backend;
var steamGame=launch.Game;
var port = 17865;
string Arg(string name,string fallback) { var i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : fallback; }

port=int.Parse(Arg("--port","17865"));
var httpsPort=int.Parse(Arg("--https-port",(port+1).ToString()));
if(port is <1 or >65535 || httpsPort is <1 or >65535 || port==httpsPort)throw new ArgumentException("Invalid local HTTP/HTTPS ports");
var followGame=args.Contains("--launch") || args.Contains("--follow-game") || steamGame!=null;
var gameExecutable=Arg("--game-exe","");
if(followGame && gameExecutable.Length==0)gameExecutable=GameProcess.ResolveGame(dataDir);
if(args.Contains("--prepare-https")||args.Contains("--renew-https")){using var prepared=LocalHttps.Load(dataDir,args.Contains("--renew-https"));Console.WriteLine(JsonSerializer.Serialize(new { thumbprint=prepared.Thumbprint,expires=prepared.NotAfter.ToUniversalTime(),publicCertificate=Path.Combine(dataDir,"localhost-certificate.cer") }));return;}
var pairingPath=Arg("--pairing-file",Path.Combine(AppContext.BaseDirectory,"pairing.key"));
if(!File.Exists(pairingPath))throw new StartupFault("pairing-missing","缺少 pairing.key，不能生成与游戏 VPK 不匹配的新密钥。请成套覆盖更新或重新安装完整包，保留个人配置与证书。");
var pairing=File.ReadAllText(pairingPath).Trim();
if(pairing.Length != 48 || !pairing.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pairing key");
var instanceScope=new InstanceScope(owner,StartupCompatibility.Scope(dataDir),StartupCompatibility.Scope(AppContext.BaseDirectory));
var backendElevated=StartupCompatibility.Elevated();
using var localControl=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(2),MaxResponseContentBufferSize=16*1024};
var controlOrigin=$"http://localhost:{port}";
var existingInstance=await InstanceCoordinator.Prepare(localControl,controlOrigin,pairing,instanceScope,adminCompatible,backendElevated);
if(existingInstance!=null){
    if(followGame){using var follow=await localControl.PostAsync(controlOrigin+"/control/"+pairing+"/follow-game",null);follow.EnsureSuccessStatusCode();}
    if(args.Contains("--settings")){using var settings=await localControl.PostAsync(controlOrigin+"/control/"+pairing+"/settings",null);settings.EnsureSuccessStatusCode();}
    if(steamGame!=null){
        if(existingInstance.Elevated&&!backendElevated)await InstanceCoordinator.LaunchGame(localControl,controlOrigin,pairing,instanceScope,steamGame);
        else{using var child=steamGame.Start(gameExecutable);}
    }
    else if(args.Contains("--launch"))Process.Start(new ProcessStartInfo("steam://rungameid/1422450"){UseShellExecute=true});
    return;
}
var config = new ConfigStore(dataDir);
var settingsToken=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args=[], ContentRootPath=AppContext.BaseDirectory, WebRootPath=Path.Combine(AppContext.BaseDirectory,"wwwroot") });
System.Security.Cryptography.X509Certificates.X509Certificate2? loadedCertificate=null;
string? certificateError=null;
try {loadedCertificate=LocalHttps.Load(dataDir);} catch(Exception e) when(e is IOException or CryptographicException or UnauthorizedAccessException){certificateError="本机证书无法读取。浏览器设置仍可用，请退出后台后修复或续期证书。";}
using var localCertificate=loadedCertificate;
var certificateDiagnostics=new CertificateDiagnostics(localCertificate,certificateError);
builder.WebHost.ConfigureKestrel(options=>{options.ListenLocalhost(port);if(certificateDiagnostics.CanServe)options.ListenLocalhost(httpsPort,listen=>listen.UseHttps(localCertificate!));});
builder.WebHost.ConfigureKestrel(options=>{options.Limits.MaxRequestBodySize=2*1024*1024;options.Limits.MaxRequestLineSize=32768;});
builder.Logging.ClearProviders();
var staticRoot=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var terms=new Terms(dataDir,Path.Combine(AppContext.BaseDirectory,"data","terms.json"));
// Warmup and translation must share the same pool; keep idle connections across the
// configurable warmup interval, while periodically refreshing DNS/network bindings.
using var handler=new SocketsHttpHandler { AutomaticDecompression=System.Net.DecompressionMethods.All,AllowAutoRedirect=false,PooledConnectionIdleTimeout=TimeSpan.FromMinutes(31),PooledConnectionLifetime=TimeSpan.FromMinutes(30) };
using var http=new HttpClient(handler) {Timeout=Timeout.InfiniteTimeSpan,MaxResponseContentBufferSize=2*1024*1024};
using var translators=new Translators(config,terms,http,audit);
await using var engine=new JobEngine(config,translators,audit);
var displayPreview=new DisplayPreviewService();
using var nativeEditor=new NativeSettingsEditor();
await using var outgoing=new OutgoingService(config,translators.TranslateOutgoing,log:audit,context:engine.OutgoingContext,translateContext:translators.TranslateOutgoing);
using var hotkey=args.Contains("--no-hotkey")?null:new NativeHotkey(config,audit);
await using var app=builder.Build();
await using var llmKeepAlive=new LlmKeepAlive(config,translators,()=>engine.Connected,audit,app.Lifetime.ApplicationStopping);
if(gameExecutable.Length==0)gameExecutable=SafeGamePath();
await using var gameLifecycle=new GameLifecycle(gameExecutable,()=>app.Lifetime.StopApplication());
string SafeGamePath(){try{return GameProcess.ResolveGame(dataDir);}catch{return "";}}
var gameLogs=new GameLogCollector(audit);
var gameMetrics=new GameMetricCollector(audit);
var backupMaintenance=new BackupMaintenance(dataDir,Path.Combine(AppContext.BaseDirectory,"Backup-Maintenance.ps1"));
var clientWasConnected=0;
using var connectionWatch=new Timer(_=>{if(!engine.Connected&&Interlocked.Exchange(ref clientWasConnected,0)==1)audit.Record("backend",LogKind.ClientLost);},null,5000,5000);
config.Changed+=()=>audit.Record("backend",LogKind.ConfigSaved,new(Binding:config.Current.OutgoingHotkey));
var bridgeTraffic=new System.Collections.Concurrent.ConcurrentDictionary<string,int>();
EvidenceSnapshot? gameEvidence=null;string? clientVersion=null;string? clientBuildId=null;int previewClientReady=0;
var gamePrivileges=new GamePrivilegeDiagnostics(expectedExecutable:gameExecutable);
int settingsBackgroundClientReady=0;
bool storeHasLlm()=>config.Secret("llm").Length>0;
object Diagnostics(bool force=false){
    var certificate=certificateDiagnostics.Inspect(force);
    var phase=certificate.State is "unavailable" or "expired" or "not-yet-valid"?"https-unavailable":certificate.State=="untrusted"?"certificate-untrusted":engine.Connected?"connected":bridgeTraffic.GetValueOrDefault("gamePost")>0?"game-stale":bridgeTraffic.GetValueOrDefault("bridgePage")>0?"bridge-no-reply":"waiting-for-game";
    return new{settingsBackgroundNativeAvailable=engine.Connected&&Volatile.Read(ref settingsBackgroundClientReady)==1&&Volatile.Read(ref clientBuildId)==BuildInfo.Id,version=BuildInfo.Version,buildId=BuildInfo.Id,clientBuildId=Volatile.Read(ref clientBuildId),featureRevision="optional-llm-keepalive-priority-warmup-installer-first-open-community",backendElevated,adminCompatible,clientVersion=Volatile.Read(ref clientVersion),port,httpsPort,certificate,phase,llmRequests=translators.Telemetry.Recent,providers=translators.Issues,configuredLlm=storeHasLlm(),gamePrivileges=gamePrivileges.Inspect(force),sending=outgoing.Diagnostics,receiving=engine.Scheduling,keepAlive=llmKeepAlive.Status,hotkeyAvailable=hotkey?.Available??false,bridgeTraffic,metrics=Volatile.Read(ref gameEvidence)?.Metrics,hotkey=hotkey?.Status,logs=audit.Status};
}
string Origin(HttpContext? ctx=null)=>ctx==null?$"http://localhost:{port}":(ctx.Request.IsHttps?"https://":"http://")+ctx.Request.Host.ToUriComponent();
string SessionCookie(HttpContext ctx)=>ctx.Request.IsHttps?"dlt-session-tls":"dlt-session";
void OpenExport(string path){if(!args.Contains("--no-shell"))try{Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+path+"\""){UseShellExecute=true});}catch{}}
string SettingsUrl()=>Origin()+"/settings/"+settingsToken;
bool Same(string? a,string b)=>a!=null && a.Length==b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a),Encoding.UTF8.GetBytes(b));
void OpenSettings()=>Process.Start(new ProcessStartInfo(SettingsUrl()){UseShellExecute=true});
void LaunchRequestedGame(){
    if(steamGame!=null){using var child=steamGame.Start(gameExecutable);}
    else if(args.Contains("--launch"))Process.Start(new ProcessStartInfo("steam://rungameid/1422450"){UseShellExecute=true});
}
app.Use(async(ctx,next)=>{
    ctx.Response.Headers["Cache-Control"]="no-store";
    ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
    ctx.Response.Headers["Referrer-Policy"]="no-referrer";
    ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'";
    if(ctx.Request.Host.Host!="localhost" && ctx.Request.Host.Host!="127.0.0.1" && ctx.Request.Host.Host!="[::1]") {ctx.Response.StatusCode=403; return;}
    var path=ctx.Request.Path.Value ?? "/";
    var trafficKind=path.StartsWith("/in-game/")?"inGamePage":path=="/app.js"?"settingsScript":path=="/"&&ctx.Request.Query["inGame"]=="1"?"inGameDocument":path.StartsWith("/bridge/")?"bridgePage":path.StartsWith("/menu/")?"menuPage":path.StartsWith("/game/")?"gamePost":path.StartsWith("/control/")&&path.EndsWith("/settings")?"openSettings":null;
    if(trafficKind!=null)bridgeTraffic.AddOrUpdate(trafficKind,1,(_,count)=>count+1);
    if(path.StartsWith("/bridge/",StringComparison.Ordinal) || path.StartsWith("/menu/",StringComparison.Ordinal))
        ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self' 'unsafe-inline'; connect-src 'self'";
    if(path.StartsWith("/api/",StringComparison.Ordinal)) {
        if(!Same(ctx.Request.Cookies[SessionCookie(ctx)],settingsToken)) {ctx.Response.StatusCode=401; return;}
        if(ctx.Request.Method!="GET" && (ctx.Request.Headers.Origin!=Origin(ctx) || ctx.Request.Headers["X-DLT-Request"]!="1")) {ctx.Response.StatusCode=403;return;}
    }
    try { await next(); }
    catch(ArgumentException ex) {audit.Record("backend",LogKind.RequestRejected,new(Reason:LogReason.InvalidData));ctx.Response.StatusCode=400; await ctx.Response.WriteAsJsonAsync(new {error=ex.Message});}
    catch(JsonException) {audit.Record("backend",LogKind.RequestRejected,new(Reason:LogReason.InvalidData));ctx.Response.StatusCode=400; await ctx.Response.WriteAsJsonAsync(new {error="JSON 格式无效"});}
    catch(Exception ex){audit.Record("backend",LogKind.RequestRejected,new(FailureType:ex.GetType().Name,HttpStatus:500));ctx.Response.StatusCode=500;await ctx.Response.WriteAsJsonAsync(new{error="后台请求失败，请导出诊断日志"});}
});
app.MapGet("/health",()=>new {app="DeadlockTranslate",version=BuildInfo.Version,buildId=BuildInfo.Id,clientBuildId=Volatile.Read(ref clientBuildId),featureRevision="optional-llm-keepalive-priority-warmup-installer-first-open-community",port,httpsPort,httpsReady=certificateDiagnostics.CanServe,certificateThumbprint=localCertificate?.Thumbprint,followingGame=gameLifecycle.Active,features=new[]{"auto-save-settings","full-screen-settings-navigation","settings-background-modes","native-settings-editor","optional-llm-keepalive","build-identity","native-nudge-telemetry","direction-profiles","bounded-discovery","term-preview","multipart-send","preset-messages","incoming-style","own-translation-display-filter","independent-feed-recovery","feed-layout-diagnostics","native-root-reflow","native-owned-birth","native-plain-text","native-layout-controller","native-direct-binding","bounded-hud-ancestry","native-prebuilt-bubble-template","native-roster-hero-identity","dual-incoming-display","hero-display-identity","game-display-preview","ten-incoming-lines","native-display-retry","independent-incoming-feed","typed-display-diagnostics","bridge-self-heal","receive-burst-threshold-2","bounded-hud-discovery","fair-incoming-polling","retired-session-protection","persisted-game-metrics","hotkey-noise-summary","outgoing-tone","outgoing-case-postprocess","stream-progress","send-context","thinking-controls","llm-usage-diagnostics","configurable-output-budget","typed-output-errors","compact-name-prompts","local-name-lists","translation-stage-timings","native-hotkey-recording","game-settings-focus","legacy-chinese-name-style","hero-aliases","api-config-diagnostics","escape-settings","outgoing-alt-space","native-chat-submit","independent-hud-intake","native-chat-target-restore","outgoing-display-mode","incoming-scope-all-players","strict-channel-wait-echo","game-linked-exit","steam-command-launch","native-chat-target-classes","multilingual-targets","persistent-diagnostics","custom-outgoing-hotkey"}});
app.MapGet("/settings/{token}",(string token,HttpContext ctx)=>{
    if(!Same(token,settingsToken)) return Results.NotFound();
    ctx.Response.Cookies.Append(SessionCookie(ctx),settingsToken,new CookieOptions{Secure=ctx.Request.IsHttps,HttpOnly=true,SameSite=SameSiteMode.Strict,IsEssential=true,Path="/"});
    return Results.Redirect("/");
});
app.MapGet("/in-game/{key}",(string key,HttpContext ctx)=>{
    if(!Same(key,pairing))return Results.NotFound();
    ctx.Response.Cookies.Append(SessionCookie(ctx),settingsToken,new CookieOptions{Secure=ctx.Request.IsHttps,HttpOnly=true,SameSite=SameSiteMode.Strict,IsEssential=true,Path="/"});
    return Results.File(Path.Combine(staticRoot,"index.html"),"text/html; charset=utf-8");
});
app.MapGet("/",(HttpContext ctx)=> Same(ctx.Request.Cookies[SessionCookie(ctx)],settingsToken) ? Results.File(Path.Combine(staticRoot,"index.html"),"text/html; charset=utf-8") : Results.Content("请通过游戏内“翻译设置”按钮或 Settings.cmd 打开此页面。","text/plain; charset=utf-8"));
app.MapGet("/app.js",()=>Results.File(Path.Combine(staticRoot,"app.js"),"text/javascript; charset=utf-8"));
app.MapGet("/style.css",()=>Results.File(Path.Combine(staticRoot,"style.css"),"text/css; charset=utf-8"));
app.MapGet("/settings-navigation.js",()=>Results.File(Path.Combine(staticRoot,"settings-navigation.js"),"text/javascript; charset=utf-8"));
app.MapGet("/settings-background.css",()=>Results.File(Path.Combine(staticRoot,"settings-background.css"),"text/css; charset=utf-8"));
app.MapGet("/settings-background.js",()=>Results.File(Path.Combine(staticRoot,"settings-background.js"),"text/javascript; charset=utf-8"));
app.MapGet("/settings-background-v3-flat.png",()=>Results.File(Path.Combine(staticRoot,"settings-background-v3-flat.png"),"image/png"));
app.MapPost("/api/native-editor",(NativeEditorDraft request)=>{
    try{return Results.Ok(nativeEditor.Create(request));}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
});
app.MapGet("/api/native-editor/{id}",(string id,string owner)=>nativeEditor.Read(id,owner) is {} value?Results.Ok(value):Results.NotFound(new{error="原生编辑已过期，请重新点击输入框。"}));
app.MapDelete("/api/native-editor/{id}",(string id,string owner)=>{nativeEditor.Cancel(id,owner);return Results.Ok();});
app.MapGet("/community-qq.png",()=>Results.File(Path.Combine(staticRoot,"community-qq.png"),"image/png"));
app.MapPost("/api/community/bilibili",()=>{
    const string homepage="https://space.bilibili.com/332089385";
    if(noShell)return Results.Conflict(new{error="本次后台禁止打开外部窗口，请复制主页链接后手动打开。"});
    try{
        // Fixed destination, user-initiated, with the existing session and Origin checks.
        // Do not navigate the game's embedded settings panel to an external website.
        using var browser=Process.Start(new ProcessStartInfo(homepage){UseShellExecute=true});
        return Results.Ok(new{opened=true});
    }catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException){
        return Results.Json(new{error="无法打开系统默认浏览器，请复制主页链接后手动打开。"},statusCode:502);
    }
});
app.MapGet("/api/config",()=>config.Public());
app.MapGet("/api/languages",()=>TranslationLanguages.Presets);
app.MapPost("/api/display-preview",(DisplayPreviewOptions value)=>!engine.Connected ? Results.Conflict(new{error="游戏聊天尚未连接，无法显示游戏内测试。请启动游戏并确认翻译已连接后再试。"}) : Volatile.Read(ref previewClientReady)==0 ? Results.Conflict(new{error="游戏内 VPK 尚不支持显示测试，请成套覆盖本次完整包并重启游戏；只替换后台不会更新游戏脚本。"}) : Results.Ok(displayPreview.Start(value)));
app.MapDelete("/api/display-preview",()=>Results.Ok(displayPreview.Stop()));
app.MapPut("/api/config",(Settings value,HttpContext ctx)=>{
    var expected=ctx.Request.Headers.IfMatch.ToString().Trim('"');
    try{if(config.Save(value,expected.Length==0?null:expected))engine.ClearCache();return Results.Json(config.Public());}
    catch(ConfigConflictException){return Results.Json(new{error="另一个设置窗口已修改配置，请处理冲突后保存；当前输入已保留。",current=config.Public()},statusCode:409);}
});
app.MapPost("/api/provider-config/check",(ApiConfigurationInput input)=>Results.Json(ApiConfigurationDiagnostics.Inspect(config,input)));
app.MapPost("/api/provider-config/test",async(ApiConfigurationInput input,HttpContext ctx)=>Results.Json(await translators.TestApiConfiguration(input,audit,ctx.RequestAborted)));
app.MapGet("/api/terms",()=>terms.All);
app.MapPost("/api/terms/preview",(TermPreviewInput input)=>Results.Json(terms.Preview(input,config.Current)));
app.MapGet("/api/outgoing",()=>new{hotkeyAvailable=hotkey?.Available??false,hotkey=hotkey?.Status,items=outgoing.Recent,stats=outgoing.Stats});
app.MapGet("/api/llm/keep-alive",()=>llmKeepAlive.Status);
app.MapPost("/api/llm/keep-alive",async(HttpContext ctx)=>Results.Ok(await llmKeepAlive.WarmOnce(ctx.RequestAborted)));
app.MapPut("/api/terms",(Term[] value)=>{terms.Save(value);engine.ClearCache();return Results.Ok(new{count=value.Length});});
app.MapGet("/api/status",()=>new{engine=engine.Stats,bridgeTraffic,terms=terms.All.Length,requests=translators.Stats,sending=outgoing.Stats,uptimeSeconds=(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,gameEvidence=Volatile.Read(ref gameEvidence),diagnostics=Diagnostics()});
app.MapGet("/api/diagnostics",()=>Diagnostics(true));
app.MapGet("/api/backups",async(bool? includeLegacy,HttpContext ctx)=>{
    try{return Results.Json(await backupMaintenance.Run(false,includeLegacy??false,null,ctx.RequestAborted));}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
});
app.MapPost("/api/backups/cleanup",async(BackupCleanupRequest request,HttpContext ctx)=>{
    // Once confirmed, finish maintenance even if the settings page is closed midway.
    try{return Results.Json(await backupMaintenance.Run(true,request.IncludeLegacy,request.PlanToken,app.Lifetime.ApplicationStopping));}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
});
app.MapGet("/api/launch-options",()=>new{normal=StartupCompatibility.LaunchOption(Environment.ProcessPath!,false),admin=StartupCompatibility.LaunchOption(Environment.ProcessPath!,true),backendElevated,adminCompatible});
var adminRestartPending=0;
app.MapPost("/api/admin-restart",()=>{
    if(noShell)return Results.Conflict(new{error="无交互模式不会请求管理员权限。"});
    if(backendElevated)return Results.Conflict(new{error="后台已经具有管理员权限，无需再次重启。"});
    if(Interlocked.Exchange(ref adminRestartPending,1)!=0)return Results.Conflict(new{error="正在等待权限确认，请勿重复点击。"});
    // UAC is user initiated; never replay pending translations after the new instance starts.
    _=Task.Run(async()=>{await Task.Delay(300);try{
        var restart=new List<string>{"--admin-compatible","--port",port.ToString(),"--https-port",httpsPort.ToString(),"--pairing-file",pairingPath};
        if(gameExecutable.Length>0)restart.AddRange(["--follow-game","--game-exe",gameExecutable]);
        using var elevated=Process.Start(StartupCompatibility.ElevationInfo(Environment.ProcessPath!,restart.ToArray(),dataDir,owner));
        if(elevated==null)throw new StartupFault("elevation-failed","管理员兼容启动未完成。");
    }catch(Exception ex){audit.Record("backend",LogKind.StartupFailed,new(Reason:StartupCompatibility.Reason(ex),Stage:"elevation",FailureType:ex.GetType().Name,Win32Error:ex is System.ComponentModel.Win32Exception w?w.NativeErrorCode:null));StartupCompatibility.Notify(ex,false);}finally{Interlocked.Exchange(ref adminRestartPending,0);}});
    return Results.Ok(new{requested=true});
});
app.MapPost("/api/hotkey/reinitialize",()=>{if(hotkey==null)return Results.Conflict(new{error="快捷键监听未启用"});hotkey.Reinitialize();return Results.Ok(new{requested=true});});
app.MapPost("/api/hotkey/test",()=>{if(hotkey==null)return Results.Conflict(new{error="快捷键监听未启用"});hotkey.BeginTest();return Results.Ok(new{seconds=15,count=hotkey.Status.TestDetected});});
app.MapPost("/api/hotkey/recording",(RecordingRequest request)=>{
    if(hotkey==null)return Results.Conflict(new{error="快捷键监听未启用，可使用按键选项设置组合。"});
    try{return Results.Ok(hotkey.BeginRecording(request.InGame));}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
});
app.MapGet("/api/hotkey/recording/{id}",(string id)=>hotkey?.Recording(id) is {} record?Results.Ok(record):Results.NotFound(new{error="录制已结束或被另一窗口替换，请重新开始。"}));
app.MapDelete("/api/hotkey/recording/{id}",(string id)=>{hotkey?.CancelRecording(id);return Results.Ok();});
app.MapPost("/api/diagnostics/export",async()=>{
    try{var path=await DiagnosticExport.Create(dataDir,audit,Diagnostics(true),config.Current);OpenExport(path);return Results.Ok(new{file=path});}
    catch(Exception ex){audit.Record("export",LogKind.ExportFailed,new(FailureType:ex.GetType().Name));return Results.Json(new{error="诊断包导出失败，请检查日志目录写入权限。"},statusCode:500);}
});
app.MapPost("/api/test",async(TestRequest req)=>{
    if(req.Text.Length is <1 or >1024 || (req.Provider!="fast" && req.Provider!="quality") || req.Direction is not ("incoming" or "outgoing")) return Results.BadRequest(new{error="测试输入无效"});
    var timer=Stopwatch.StartNew(); var c=config.Current;
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(req.Provider=="quality"?c.LlmTimeoutSeconds:c.FastTimeoutSeconds));
    try { var result=req.Direction=="outgoing"?await translators.TranslateOutgoing(req.Text,c with{OutgoingMode=req.Provider},req.Provider=="fast",timeout.Token):req.Provider=="quality"?await translators.Quality(req.Text,[],c,timeout.Token):await translators.Fast(req.Text,c,timeout.Token);return Results.Ok(new{translation=result,elapsedMs=timer.ElapsedMilliseconds}); }
    catch(OperationCanceledException) {audit.Record("translation",LogKind.TranslationFailed,new(Reason:LogReason.Timeout,Provider:req.Provider=="quality"?"quality":c.FastProvider,Stage:"provider-test",FailureType:"OperationCanceledException",ElapsedMs:timer.ElapsedMilliseconds));return Results.Json(new{error="请求超时",elapsedMs=timer.ElapsedMilliseconds},statusCode:504);}
    catch(Exception ex) {var failure=ex as ProviderRequestException;var h=ex as HttpRequestException;var reason=failure?.Reason??(h?.StatusCode==System.Net.HttpStatusCode.TooManyRequests?LogReason.RateLimit:h?.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden?LogReason.Unauthorized:h!=null?LogReason.Network:LogReason.Other);audit.Record("translation",LogKind.TranslationFailed,new(Reason:reason,Provider:req.Provider=="quality"?"quality":c.FastProvider,Stage:failure?.Stage??"provider-test",HttpStatus:(int?)h?.StatusCode,FailureType:ex.GetType().Name,ElapsedMs:timer.ElapsedMilliseconds));return Results.Json(new{error=failure?.Message??(ex is InvalidOperationException || (ex is InvalidDataException && ex.Message.StartsWith("精翻"))?ex.Message:h?.StatusCode!=null?"翻译服务 HTTP "+(int)h.StatusCode:"网络或服务返回异常，请检查服务配置"),provider=req.Provider=="quality"?"quality":c.FastProvider,stage=failure?.Stage??"provider-test",httpStatus=(int?)h?.StatusCode,failureType=ex.GetType().Name,elapsedMs=timer.ElapsedMilliseconds},statusCode:502);}
});
app.MapPost("/api/stop",(IHostApplicationLifetime life)=>{_ = Task.Run(async()=>{await Task.Delay(300);life.StopApplication();});return Results.Ok();});
var lastOpen=0L;
var gameExchangeGate=new object();
app.MapPost("/game/{key}",(string key,GameRequest req,HttpContext ctx)=>{
    if(!Same(key,pairing) || ctx.Request.Headers.Origin!=Origin(ctx)) return Results.Unauthorized();
    // Keep the receive engine, outgoing queue, focus and diagnostics on one owner.
    // The route is synchronous and never awaits provider requests under this gate.
    lock(gameExchangeGate) {
    if(req.ClientVersion!=null&&(req.ClientVersion.Length>32||!req.ClientVersion.All(c=>char.IsAsciiDigit(c)||c=='.')))throw new ArgumentException("客户端版本字段过长");
    if(req.ClientBuildId!=null&&(req.ClientBuildId.Length>40||req.ClientBuildId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='.'&&c!='-')))throw new ArgumentException("无效的构建编号");
    var result=engine.Exchange(req.Session,req.Messages??[],req.Poll??[]);
    if(!engine.IsCurrentSession(req.Session))return Results.Conflict(new{error="stale-session"});
    if(req.OpenSettings && Environment.TickCount64-lastOpen>2000) {lastOpen=Environment.TickCount64;OpenSettings();}
    if(req.Metrics!=null) {
        // Current-process counters only; no synchronous disk I/O on the game bridge.
        Volatile.Write(ref gameEvidence,new EvidenceSnapshot(DateTime.UtcNow,req.Metrics));
        gameMetrics.Accept(req.Session,req.Metrics,Environment.TickCount64);
    }
    if(req.ClientVersion!=null){if(req.ClientVersion!=Volatile.Read(ref clientVersion)&&req.ClientVersion!=BuildInfo.Version)audit.Record("backend",LogKind.ClientVersionMismatch,new(Version:req.ClientVersion));Volatile.Write(ref clientVersion,req.ClientVersion);}
    if(req.ClientBuildId!=null&&req.ClientBuildId!=Volatile.Read(ref clientBuildId)&&req.ClientBuildId!=BuildInfo.Id)audit.Record("backend",LogKind.ClientVersionMismatch,new(Version:req.ClientVersion,BuildId:req.ClientBuildId));
    Volatile.Write(ref clientBuildId,req.ClientBuildId);
    hotkey?.UpdateQuickSession(req.Session,req.ClientBuildId==BuildInfo.Id&&req.MultipartSupported,req.ChatChannel??"");
    Volatile.Write(ref previewClientReady,req.DisplayPreviewSupported?1:0);
    Volatile.Write(ref settingsBackgroundClientReady,req.SettingsBackgroundSupported?1:0);
    if(Interlocked.Exchange(ref clientWasConnected,1)==0)audit.Record("backend",LogKind.ClientConnected,new(Version:req.ClientVersion));
    var logAck=gameLogs.Accept(req.Session,req.Logs??[]);
    hotkey?.UpdateFocus((req.ServerId==null||req.ServerId==audit.RunId)&&req.ChatOpen);
    hotkey?.UpdateSettings((req.ServerId==null||req.ServerId==audit.RunId)&&req.SettingsOpen==true);
    var staleServer=!string.IsNullOrEmpty(req.ServerId)&&req.ServerId!=audit.RunId;
    if(staleServer&&req.Outgoing is {Length:>0})audit.Record("sending",LogKind.SendFailed,new(Reason:LogReason.ServerChanged,Stage:"rejected",Count:req.Outgoing.Length));
    var nativeEditorResult=req.NativeEditor==null?null:nativeEditor.Dispatch(req.NativeEditor,!staleServer&&req.ClientBuildId==BuildInfo.Id&&req.SettingsOpen==true);
    var outgoingResults=outgoing.Exchange(req.Session,staleServer?[]:req.Outgoing??[],staleServer?[]:req.OutgoingSent??[],staleServer?[]:req.OutgoingWaiting??[],staleServer?[]:req.OutgoingRefused??[],staleServer?[]:req.OutgoingPartAcks??[],staleServer?[]:hotkey?.TakeQuickMessages(req.Session)??[],req.MultipartSupported,staleServer?[]:req.OutgoingStatus??[]);
    return Results.Json(new{seq=req.Seq,session=req.Session,serverId=audit.RunId,logAck,nativeEditor=nativeEditorResult,displayPreview=displayPreview.Snapshot(),results=result,outgoing=outgoingResults,translationProgress=translators.Telemetry.Summary,hotkey=hotkey?.PendingSequence(req.ServerId==audit.RunId?req.HotkeySeen:0)??0,hotkeyAvailable=hotkey?.Available??false,capabilities=new[]{"outgoing-status-v1","native-settings-editor","multipart-send","preset-messages","outgoing-display-mode","incoming-scope-all-players","outgoing-party-channel","multilingual-targets","diagnostic-logs","custom-hotkey"},config=new{quickMessagesEnabled=config.Current.OutgoingEnabled&&config.Current.QuickMessages.Any(q=>q.Enabled)&&req.ClientBuildId==BuildInfo.Id,config.Current.Enabled,config.Current.TranslateOwn,config.Current.IncomingScope,config.Current.IncomingShowOwnTranslations,config.Current.IncomingDisplayMode,config.Current.IncomingFeedSeconds,config.Current.IncomingFeedLines,config.Current.IncomingShowHeroName,config.Current.IncomingShowAccountName,config.Current.IncomingHeroNamePosition,config.Current.IncomingOpacity,config.Current.IncomingFeedWidth,config.Current.IncomingFeedPosition,config.Current.IncomingFeedOffsetX,config.Current.IncomingFeedOffsetY,config.Current.FontSize,config.Current.SettingsBackgroundMode,config.Current.Transport,config.Current.ScanIntervalMs,config.Current.OutgoingEnabled,config.Current.OutgoingHotkey,outgoingHotkeyLabel=HotkeyChord.Parse(config.Current.OutgoingHotkey).Label,config.Current.OutgoingTimeoutSeconds,config.Current.OutgoingDisplayMode,config.Current.OutgoingSeparateBilingual,config.Current.OutgoingSplitLong,config.Current.OutgoingQueueLimit,config.Current.OutgoingUseContext,config.Current.IncomingTargetLanguage,config.Current.IncomingCustomLanguage,config.Current.IncomingCustomLanguageCode,config.Current.OutgoingTargetLanguage,config.Current.OutgoingCustomLanguage,config.Current.OutgoingCustomLanguageCode},configuredLlm=config.Secret("llm").Length>0});
    }
});
app.MapGet("/bridge/{key}",(string key)=>{
    if(!Same(key,pairing)) return Results.NotFound();
    // Fragment navigation reuses one document; navigation mode remains available for engine compatibility.
    var script="""
<!doctype html><meta charset="utf-8"><title>DLT:ready</title><script>
let busy=false, last='';
async function dispatch(){
 const raw=location.hash ? decodeURIComponent(location.hash.slice(1)) : new URLSearchParams(location.search).get('d');
 if(!raw||raw===last||busy)return; busy=true;last=raw;
 try{const data=JSON.parse(raw);
 const res=await fetch('/game/PAIRING',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
 if(!res.ok)throw new Error('HTTP '+res.status);
 document.title='DLT:'+JSON.stringify(await res.json());
 }catch(e){document.title='DLT:'+JSON.stringify({error:'bridge-request-failed'});}finally{busy=false;dispatch();}
}
addEventListener('hashchange',dispatch);dispatch();
</script>
""".Replace("PAIRING",pairing);
    return Results.Content(script,"text/html; charset=utf-8");
});
app.MapPost("/control/{key}/settings",(string key,HttpContext ctx)=>{if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();OpenSettings();return Results.Ok();});
app.MapGet("/menu/{key}",(string key)=>{
    if(!Same(key,pairing)) return Results.NotFound();
    return Results.Content("<!doctype html><meta charset=\"utf-8\"><title>DLTMENU:loading</title><script>fetch('/control/"+pairing+"/settings',{method:'POST'}).then(r=>{document.title=r.ok?'DLTMENU:opened':'DLTMENU:error';}).catch(()=>document.title='DLTMENU:error');</script>","text/html; charset=utf-8");
});
app.MapPost("/control/{key}/session",(string key,HttpContext ctx)=>{if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();return Results.Ok(new{url=SettingsUrl()});});
app.MapPost("/control/{key}/instance",(string key,InstanceScope requested,HttpContext ctx)=>{
    if(!Same(key,pairing)||(ctx.Request.Headers.Origin.Count>0&&ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();
    return InstanceCoordinator.Matches(requested,instanceScope)?Results.Ok(new InstanceInfo(BuildInfo.Version,backendElevated,!backendElevated)):Results.Conflict();
});
app.MapPost("/control/{key}/launch-game",(string key,LaunchGameRequest requested,HttpContext ctx)=>{
    if(!Same(key,pairing)||(ctx.Request.Headers.Origin.Count>0&&ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();
    if(!InstanceCoordinator.Matches(requested.Scope,instanceScope)||requested.Game==null||gameExecutable.Length==0)return Results.StatusCode(403);
    try{using var child=requested.Game.Start(gameExecutable);gameLifecycle.Arm();return Results.Ok();}
    catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==740){audit.Record("backend",LogKind.StartupFailed,new(Reason:LogReason.ElevationRequired,Stage:"steam",Win32Error:740));return Results.Conflict();}
});
app.MapPost("/control/{key}/admin-handoff",(string key,InstanceScope requested,HttpContext ctx)=>{
    if(!Same(key,pairing)||(ctx.Request.Headers.Origin.Count>0&&ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();
    if(backendElevated||!InstanceCoordinator.Matches(requested,instanceScope))return Results.Conflict();
    audit.Record("backend",LogKind.AdminHandoff,new(Stage:"handoff"));
    _=Task.Run(async()=>{await Task.Delay(300);app.Lifetime.StopApplication();});return Results.Ok();
});
app.MapPost("/control/{key}/follow-game",(string key,HttpContext ctx)=>{
    if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin(ctx)))return Results.NotFound();
    if(gameExecutable.Length==0)return Results.Conflict();
    gameLifecycle.Arm();return Results.Ok();
});
try {
    await app.StartAsync();
    if(followGame)gameLifecycle.Arm();
    if(args.Contains("--settings")) OpenSettings();
    LaunchRequestedGame();
    try { Process.GetCurrentProcess().PriorityClass=ProcessPriorityClass.BelowNormal; }catch { }
    await app.WaitForShutdownAsync();gameMetrics.Stop();audit.Record("backend",LogKind.Stopped);
} catch(IOException) {
    // A startup race may remain. Never attach to an unverified listener.
    var racedInstance=await InstanceCoordinator.Prepare(localControl,controlOrigin,pairing,instanceScope,adminCompatible,backendElevated)??throw new StartupFault("port-busy","本地 HTTP 或 HTTPS 端口已被占用。请导出日志检查，不会关闭其他程序。");
    if(followGame){using var follow=await localControl.PostAsync(controlOrigin+"/control/"+pairing+"/follow-game",null);follow.EnsureSuccessStatusCode();}
    if(args.Contains("--settings")){using var settings=await localControl.PostAsync(controlOrigin+"/control/"+pairing+"/settings",null);settings.EnsureSuccessStatusCode();}
    if(steamGame!=null){
        if(racedInstance.Elevated&&!backendElevated)await InstanceCoordinator.LaunchGame(localControl,controlOrigin,pairing,instanceScope,steamGame);
        else{using var child=steamGame.Start(gameExecutable);}
    }
    else LaunchRequestedGame();
}

} catch(Exception ex){audit.Record("backend",LogKind.StartupFailed,new(Reason:StartupCompatibility.Reason(ex),Stage:ex is StartupFault?"instance":"startup",FailureType:ex.GetType().Name,Win32Error:ex is System.ComponentModel.Win32Exception w?w.NativeErrorCode:null));await audit.FlushAsync();StartupCompatibility.Notify(ex,noShell);Environment.ExitCode=1;}
public record TestRequest(string Provider,string Text,string Direction="incoming");
public record RecordingRequest(bool InGame=false);
public record Metrics(int Scans,int Rows,int Submitted,int Rendered,int Bridges,int Errors,double MaxScanMs,int RegularRows=0,int HudRows=0,int LobbyRows=0,int SkippedSelf=0,int SkippedUnknown=0,int SendAttempts=0,int SendErrors=0,int ChannelWaits=0,int ChannelRetargets=0,int ChannelFaults=0,int HotkeyDrops=0,int ClassChannelReads=0,int LocalEchoReused=0,int HudContainers=0,int HudRebinds=0,int HudRendered=0,int HudRecovered=0,int HudHostRepairs=0,int HudHiddenRepairs=0,int HudBindings=0,int HudExpiredResults=0,int HudDeferredScans=0,int GameLogDrops=0,int HudWaits=0,int HudRetryRecovered=0,int HudNativeExpired=0,int HudPanelGone=0,int HudRetryDropped=0,int FeedRendered=0,int FeedExpired=0,int FeedDropped=0,PerformanceSample? Performance=null);
public record GameRequest(string Session,int Seq,Incoming[]? Messages,string[]? Poll,bool OpenSettings=false,Metrics? Metrics=null,bool ChatOpen=false,long HotkeySeen=0,OutgoingInput[]? Outgoing=null,string[]? OutgoingSent=null,string[]? OutgoingWaiting=null,OutgoingRefusal[]? OutgoingRefused=null,string? ClientVersion=null,GameLogEvent[]? Logs=null,string? ServerId=null,bool? SettingsOpen=null,bool DisplayPreviewSupported=false,string? ClientBuildId=null,bool MultipartSupported=false,OutgoingPartAck[]? OutgoingPartAcks=null,string? ChatChannel=null,NativeEditorCommand? NativeEditor=null,OutgoingStatus[]? OutgoingStatus=null,bool SettingsBackgroundSupported=false);

public record EvidenceSnapshot(DateTime At,Metrics Metrics);
