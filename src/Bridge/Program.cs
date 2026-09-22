using DeadlockTranslate;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var launch=SteamGameLaunch.Split(args);
args=launch.Backend;
var steamGame=launch.Game;
var port = 17865;
string Arg(string name,string fallback) { var i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : fallback; }
var dataDir = Arg("--data-dir",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate"));
port=int.Parse(Arg("--port","17865"));
var followGame=args.Contains("--launch") || args.Contains("--follow-game") || steamGame!=null;
var gameExecutable=Arg("--game-exe","");
if(followGame && gameExecutable.Length==0)gameExecutable=GameProcess.ResolveGame(dataDir);
var config = new ConfigStore(dataDir);
var pairingPath=Arg("--pairing-file",Path.Combine(AppContext.BaseDirectory,"pairing.key"));
if(!File.Exists(pairingPath)) { Directory.CreateDirectory(Path.GetDirectoryName(pairingPath)!); File.WriteAllText(pairingPath,Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()); }
var pairing=File.ReadAllText(pairingPath).Trim();
if(pairing.Length != 48 || !pairing.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pairing key");
var settingsToken=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args=[], ContentRootPath=AppContext.BaseDirectory, WebRootPath=Path.Combine(AppContext.BaseDirectory,"wwwroot") });
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.WebHost.ConfigureKestrel(options=>{options.Limits.MaxRequestBodySize=2*1024*1024;options.Limits.MaxRequestLineSize=32768;});
builder.Logging.ClearProviders();
var staticRoot=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var terms=new Terms(dataDir,Path.Combine(AppContext.BaseDirectory,"data","terms.json"));
using var handler=new HttpClientHandler { AutomaticDecompression=System.Net.DecompressionMethods.All,AllowAutoRedirect=false };
using var http=new HttpClient(handler) {Timeout=Timeout.InfiniteTimeSpan,MaxResponseContentBufferSize=2*1024*1024};
using var translators=new Translators(config,terms,http);
await using var engine=new JobEngine(config,translators);
await using var outgoing=new OutgoingService(config,translators.ToEnglish);
using var hotkey=args.Contains("--no-hotkey")?null:new NativeHotkey(config);
var app=builder.Build();
if(gameExecutable.Length==0)gameExecutable=SafeGamePath();
await using var gameLifecycle=new GameLifecycle(gameExecutable,()=>app.Lifetime.StopApplication());
string SafeGamePath(){try{return GameProcess.ResolveGame(dataDir);}catch{return "";}}
var bridgeTraffic=new System.Collections.Concurrent.ConcurrentDictionary<string,int>();
string Origin()=>$"http://localhost:{port}";
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
        if(!Same(ctx.Request.Cookies["dlt-session"],settingsToken)) {ctx.Response.StatusCode=401; return;}
        if(ctx.Request.Method!="GET" && (ctx.Request.Headers.Origin!=Origin() || ctx.Request.Headers["X-DLT-Request"]!="1")) {ctx.Response.StatusCode=403;return;}
    }
    try { await next(); }
    catch(ArgumentException ex) {ctx.Response.StatusCode=400; await ctx.Response.WriteAsJsonAsync(new {error=ex.Message});}
    catch(JsonException) {ctx.Response.StatusCode=400; await ctx.Response.WriteAsJsonAsync(new {error="JSON 格式无效"});}
});
app.MapGet("/health",()=>new {app="DeadlockTranslate",version="0.4.0",port,followingGame=gameLifecycle.Active,features=new[]{"escape-settings","outgoing-alt-space","native-chat-submit","independent-hud-intake","native-chat-target-restore","outgoing-display-mode","incoming-scope-all-players","strict-channel-wait-echo","game-linked-exit","steam-command-launch","native-chat-target-classes"}});
app.MapGet("/settings/{token}",(string token,HttpContext ctx)=>{
    if(!Same(token,settingsToken)) return Results.NotFound();
    ctx.Response.Cookies.Append("dlt-session",settingsToken,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,IsEssential=true,Path="/"});
    return Results.Redirect("/");
});
app.MapGet("/in-game/{key}",(string key,HttpContext ctx)=>{
    if(!Same(key,pairing))return Results.NotFound();
    ctx.Response.Cookies.Append("dlt-session",settingsToken,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,IsEssential=true,Path="/"});
    return Results.Redirect("/?inGame=1");
});
app.MapGet("/",(HttpContext ctx)=> Same(ctx.Request.Cookies["dlt-session"],settingsToken) ? Results.File(Path.Combine(staticRoot,"index.html"),"text/html; charset=utf-8") : Results.Content("请通过游戏内“翻译设置”按钮或 Settings.cmd 打开此页面。","text/plain; charset=utf-8"));
app.MapGet("/app.js",()=>Results.File(Path.Combine(staticRoot,"app.js"),"text/javascript; charset=utf-8"));
app.MapGet("/style.css",()=>Results.File(Path.Combine(staticRoot,"style.css"),"text/css; charset=utf-8"));
app.MapGet("/api/config",()=>config.Public());
app.MapPut("/api/config",(Settings value)=>{config.Save(value);engine.ClearCache();return Results.Json(config.Public());});
app.MapGet("/api/terms",()=>terms.All);
app.MapGet("/api/outgoing",()=>new{hotkeyAvailable=hotkey?.Available??false,items=outgoing.Recent,stats=outgoing.Stats});
app.MapPut("/api/terms",(Term[] value)=>{terms.Save(value);engine.ClearCache();return Results.Ok(new{count=value.Length});});
app.MapGet("/api/status",()=>new{engine=engine.Stats,bridgeTraffic,terms=terms.All.Length,requests=translators.Stats,sending=outgoing.Stats,uptimeSeconds=(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,gameEvidence=File.Exists(Path.Combine(dataDir,"game-evidence.json"))?JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(dataDir,"game-evidence.json"))):(JsonElement?)null});
app.MapPost("/api/test",async(TestRequest req)=>{
    if(req.Text.Length is <1 or >1024 || (req.Provider!="fast" && req.Provider!="quality")) return Results.BadRequest(new{error="测试输入无效"});
    var timer=Stopwatch.StartNew(); var c=config.Current;
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(req.Provider=="quality"?c.LlmTimeoutSeconds:c.FastTimeoutSeconds));
    try { var result=req.Provider=="quality"?await translators.Quality(req.Text,[],c,timeout.Token):await translators.Fast(req.Text,c,timeout.Token);return Results.Ok(new{translation=result,elapsedMs=timer.ElapsedMilliseconds}); }
    catch(OperationCanceledException) {return Results.Json(new{error="请求超时",elapsedMs=timer.ElapsedMilliseconds},statusCode:504);}
    catch(Exception ex) {return Results.Json(new{error=ex is InvalidOperationException || (ex is InvalidDataException && ex.Message.StartsWith("精翻"))?ex.Message:ex is HttpRequestException h && h.StatusCode.HasValue?"翻译服务 HTTP "+(int)h.StatusCode:"网络或服务返回异常，请检查服务配置",failureType=ex.GetType().Name,elapsedMs=timer.ElapsedMilliseconds},statusCode:502);}
});
app.MapPost("/api/stop",(IHostApplicationLifetime life)=>{_ = Task.Run(async()=>{await Task.Delay(300);life.StopApplication();});return Results.Ok();});
var lastOpen=0L;
app.MapPost("/game/{key}",(string key,GameRequest req,HttpContext ctx)=>{
    if(!Same(key,pairing) || ctx.Request.Headers.Origin!=Origin()) return Results.Unauthorized();
    if(req.OpenSettings && Environment.TickCount64-lastOpen>2000) {lastOpen=Environment.TickCount64;OpenSettings();}
    if(req.Metrics!=null) {
        // Persist counters only. Never persist chat text, player identifiers or API payloads.
        ConfigStore.WriteAtomic(Path.Combine(dataDir,"game-evidence.json"),JsonSerializer.SerializeToUtf8Bytes(new{at=DateTime.UtcNow,req.Metrics},ConfigStore.Json));
    }
    var result=engine.Exchange(req.Session,req.Messages??[],req.Poll??[]);
    hotkey?.UpdateFocus(req.ChatOpen);
    var outgoingResults=outgoing.Exchange(req.Session,req.Outgoing??[],req.OutgoingSent??[],req.OutgoingWaiting??[],req.OutgoingRefused??[]);
    return Results.Json(new{seq=req.Seq,serverId=settingsToken[..12],results=result,outgoing=outgoingResults,hotkey=hotkey?.PendingSequence(req.HotkeySeen)??0,hotkeyAvailable=hotkey?.Available??false,capabilities=new[]{"outgoing-display-mode","incoming-scope-all-players","outgoing-party-channel"},config=new{config.Current.Enabled,config.Current.TranslateOwn,config.Current.IncomingScope,config.Current.FontSize,config.Current.Transport,config.Current.ScanIntervalMs,config.Current.OutgoingEnabled,config.Current.OutgoingTimeoutSeconds,config.Current.OutgoingDisplayMode,config.Current.OutgoingQueueLimit},configuredLlm=config.Secret("llm").Length>0});
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
 }catch(e){document.title='DLT:'+JSON.stringify({error:String(e)});}finally{busy=false;}
}
addEventListener('hashchange',dispatch);dispatch();
</script>
""".Replace("PAIRING",pairing);
    return Results.Content(script,"text/html; charset=utf-8");
});
app.MapPost("/control/{key}/settings",(string key,HttpContext ctx)=>{if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin()))return Results.NotFound();OpenSettings();return Results.Ok();});
app.MapGet("/menu/{key}",(string key)=>{
    if(!Same(key,pairing)) return Results.NotFound();
    return Results.Content("<!doctype html><meta charset=\"utf-8\"><title>DLTMENU:loading</title><script>fetch('/control/"+pairing+"/settings',{method:'POST'}).then(r=>{document.title=r.ok?'DLTMENU:opened':'DLTMENU:error';}).catch(()=>document.title='DLTMENU:error');</script>","text/html; charset=utf-8");
});
app.MapPost("/control/{key}/session",(string key,HttpContext ctx)=>{if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin()))return Results.NotFound();return Results.Ok(new{url=SettingsUrl()});});
app.MapPost("/control/{key}/follow-game",(string key,HttpContext ctx)=>{
    if(!Same(key,pairing) || (ctx.Request.Headers.Origin.Count>0 && ctx.Request.Headers.Origin!=Origin()))return Results.NotFound();
    if(gameExecutable.Length==0)return Results.Conflict();
    gameLifecycle.Arm();return Results.Ok();
});
try {
    await app.StartAsync();
    if(followGame)gameLifecycle.Arm();
    if(args.Contains("--settings")) OpenSettings();
    LaunchRequestedGame();
    try { Process.GetCurrentProcess().PriorityClass=ProcessPriorityClass.BelowNormal; }catch { }
    await app.WaitForShutdownAsync();
} catch(IOException) {
    // A second launcher only talks to an instance with the same pairing key.
    using var second=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
    try {
        var health=await second.GetStringAsync(Origin()+"/health");
        if(!health.Contains("DeadlockTranslate")) return;
        if(followGame){var follow=await second.PostAsync(Origin()+"/control/"+pairing+"/follow-game",null);follow.EnsureSuccessStatusCode();}
        if(args.Contains("--settings")) await second.PostAsync(Origin()+"/control/"+pairing+"/settings",null);
        LaunchRequestedGame();
    } catch { }
}
public record TestRequest(string Provider,string Text);
public record Metrics(int Scans,int Rows,int Submitted,int Rendered,int Bridges,int Errors,double MaxScanMs,int RegularRows=0,int HudRows=0,int LobbyRows=0,int SkippedSelf=0,int SkippedUnknown=0,int SendAttempts=0,int SendErrors=0,int ChannelWaits=0,int ChannelRetargets=0,int ChannelFaults=0,int HotkeyDrops=0,int ClassChannelReads=0);
public record GameRequest(string Session,int Seq,Incoming[]? Messages,string[]? Poll,bool OpenSettings=false,Metrics? Metrics=null,bool ChatOpen=false,long HotkeySeen=0,OutgoingInput[]? Outgoing=null,string[]? OutgoingSent=null,string[]? OutgoingWaiting=null,OutgoingRefusal[]? OutgoingRefused=null);
