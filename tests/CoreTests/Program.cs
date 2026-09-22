using DeadlockTranslate;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var temp=Path.Combine(root,"artifacts","core-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
int passed=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
var config=new ConfigStore(temp);
var neverStarted=new GameExitDeadline(0);
Check(!neverStarted.ShouldStop(false,179999) && neverStarted.ShouldStop(false,180000),"launch timeout exits only after three minutes without a game");
var gameExit=new GameExitDeadline(0);
Check(!gameExit.ShouldStop(true,0) && !gameExit.ShouldStop(false,2000) && !gameExit.ShouldStop(false,11999) && gameExit.ShouldStop(false,12000),"game exit requires ten seconds of confirmed absence");
var restartGrace=new GameExitDeadline(0);
restartGrace.ShouldStop(true,0);restartGrace.ShouldStop(false,2000);
Check(!restartGrace.ShouldStop(true,10000) && !restartGrace.ShouldStop(false,12000),"a game restart resets the exit grace period");
Check(!restartGrace.ShouldStop(null,25000) && !restartGrace.ShouldStop(false,26000),"process access failure cannot count as a confirmed exit");
Check(GameProcess.IsRunning(Environment.ProcessPath!)==true,"process detection identifies the exact executable path");
Check(GameProcess.IsRunning(Path.Combine(temp,Path.GetFileName(Environment.ProcessPath!)))==false,"another path with the same executable name is not the game");
Check(GameProcess.IsRunning("")==null,"missing game path is unknown rather than proof of exit");
var forwarded=new[]{"--port","17868","--steam-game",Environment.ProcessPath!,"--launch","--port","9999","a b","quote\"value","尾部\\","& literal"};
var split=SteamGameLaunch.Split(forwarded);
Check(split.Backend.SequenceEqual(["--port","17868"]) && split.Game!.Arguments.SequenceEqual(forwarded[4..]),"Steam game arguments cannot become backend flags");
Check(SteamGameLaunch.Split(["--launch"]).Game==null,"legacy launcher still selects Steam URI mode");
bool missingCommand=false;try{SteamGameLaunch.Split(["--steam-game"]);}catch(ArgumentException){missingCommand=true;}Check(missingCommand,"missing Steam command is rejected");
bool recursiveLaunch=false;try{split.Game!.Start(Environment.ProcessPath!);}catch(InvalidOperationException){recursiveLaunch=true;}Check(recursiveLaunch,"recursive wrapper launch is rejected without starting a process");
config.Save(config.Current with{LlmKey="FAKE-TEST-SECRET",LlmConcurrency=1,FastTimeoutSeconds=2,LlmTimeoutSeconds=5});
Check(!File.ReadAllText(Path.Combine(temp,"settings.json")).Contains("FAKE-TEST-SECRET"),"no secret in settings JSON");
Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp,"secrets.dpapi"))).Contains("FAKE-TEST-SECRET"),"secret encrypted on disk");
Check(new ConfigStore(temp).Secret("llm")=="FAKE-TEST-SECRET","DPAPI round trip");
config.Save(config.Current with{FontSize=18});Check(config.Secret("llm")=="FAKE-TEST-SECRET","blank secret preserves existing credential");
var terms=new Terms(temp,Path.Combine(root,"src/Bridge/data/terms.json"));
Check(terms.All.Length>=350,"game terminology imported");
Check(terms.Exact("don't fight!",true)=="别打","tactical phrase exact match");
Check(terms.Exact("назад",true)=="撤退","Russian phrase exact match");
Check(terms.Protect("wait seven seconds",true).Map.Count==0,"ambiguous Seven remains conversational");
Check(terms.Protect("Seven has no ult",true,true).Map.Values.Any(v=>v.Contains("柒")),"Seven with tactical context is protected");
Check(terms.Protect("my pocket is empty",true,true).Map.Count==0,"ordinary pocket is not a hero");
var canonical=new Dictionary<string,string>{{"DLTERM0X","柒（Seven）"}};
Check(Terms.NormalizeQualityNames("柒没大招",canonical)=="柒（Seven）没大招","bilingual name normalized after translation");
bool badName=false;try{Terms.NormalizeQualityNames("棒（Seven）没大招",canonical);}catch(InvalidDataException){badName=true;}Check(badName,"incorrect name pair cannot produce nested labels");
var protectedText=terms.Protect("Bebop and Bebop",true);
Check(protectedText.Map.Count==2,"repeated hero occurrences protected independently");
Check(Terms.Restore(protectedText.Text,protectedText.Map).Contains("Bebop"),"hero placeholders restored");
bool threw=false;try{Terms.Restore("lost placeholder",protectedText.Map);}catch(InvalidDataException){threw=true;}Check(threw,"damaged placeholders rejected");
Check(terms.Protect("Bebopper",true).Map.Count==0,"no substring hero replacement");
var terminologyCases=JsonSerializer.Deserialize<string?[][]>(File.ReadAllText(Path.Combine(root,"tests/terms-cases.json")))!;
foreach(var termCase in terminologyCases){
    var text=termCase[1]!;
    string? actual=termCase[0] switch {
        "exact"=>terms.Exact(text,false),
        "untouched"=>terms.Protect(text,false).Text,
        "outgoing"=>terms.PrepareOutgoing(text,terms.MatchOutgoing(text)),
        _=>throw new Exception("Invalid term fixture")
    };
    Check(actual==termCase[2],"terminology "+termCase[0]+": "+text+" => "+actual);
}
Check(terms.Match("QSR and qsrhelper").Count(t=>t.Source.Equals("qsr",StringComparison.OrdinalIgnoreCase))==1,"abbreviation matching respects word boundaries");
Check(terms.Protect("buy QSR and QSR",true).Map.Values.All(x=>x=="魔力装填（Quicksilver Reload）"),"repeated equipment abbreviation keeps the canonical bilingual name");
var mixedSeven=terms.Protect("Seven has no ult, wait seven seconds",false,true,true);
Check(Terms.Restore(mixedSeven.Text,mixedSeven.Map)=="柒 has no ult, wait seven seconds","hero context does not convert a later ordinary occurrence of the same word");
Check(terms.All.All(t=>!t.Source.Contains("#|")),"Russian grammatical metadata is not part of a typed hero name");
var glossaryConfig=new ConfigStore(Path.Combine(temp,"glossary-provider"));
glossaryConfig.Save(glossaryConfig.Current with{LlmKey="FAKE-LOCAL-KEY",LlmBaseUrl="http://127.0.0.1:1/v1",OutgoingMode="quality"});
var capturedHttp=new CaptureHttp();using var glossaryHttp=new HttpClient(capturedHttp);
var glossaryTranslator=new Translators(glossaryConfig,terms,glossaryHttp);
capturedHttp.Reply="Seven has no ult. Buy Quicksilver Reload.";
await glossaryTranslator.ToEnglish("老七没大招，买水银装填",glossaryConfig.Current,false,CancellationToken.None);
using(var captured=JsonDocument.Parse(capturedHttp.Body!)){
    using var payload=JsonDocument.Parse(captured.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    Check(payload.RootElement.GetProperty("current").GetString()=="Seven没大招，买Quicksilver Reload","outgoing provider receives canonical names for Chinese nicknames");
    Check(payload.RootElement.GetProperty("glossary").EnumerateArray().Any(x=>x.GetProperty("chinese").GetString()=="老七"),"outgoing provider receives the matched nickname glossary");
}
capturedHttp.Reply="魔力装填（Quicksilver Reload）还在冷却。";
var glossaryQuality=await glossaryTranslator.Quality("QSR on cooldown",[],glossaryConfig.Current,CancellationToken.None);
Check(glossaryQuality==capturedHttp.Reply,"incoming quality path preserves the expanded abbreviation's canonical name");
var termTimer=System.Diagnostics.Stopwatch.StartNew();
for(var pass=0;pass<100;pass++)terms.Match("mcg no ult, buy qsr, defend blue, 7 seconds");
termTimer.Stop();
File.WriteAllText(Path.Combine(root,"artifacts/terminology-tests.json"),JsonSerializer.Serialize(new{cases=terminologyCases.Length+2,totalTerms=terms.All.Length,match100Milliseconds=termTimer.Elapsed.TotalMilliseconds,at=DateTime.UtcNow,scope="deterministic fixtures; no real translation API or game FPS measurement"},ConfigStore.Json));
var fake=new Fake();await using var engine=new JobEngine(config,fake);
var inputs=Enumerable.Range(1,6).Select(i=>new Incoming(i.ToString(),"msg "+i,"","team")).ToArray();
engine.Exchange("session-a",inputs,[]);
var first=await Poll(engine,"session-a",inputs.Select(i=>i.Id).ToArray(),v=>v.Any(x=>x.Stage=="fast"));
Check(first.Any(x=>x.Stage=="fast"),"fast results available before quality");
var all=await Poll(engine,"session-a",inputs.Select(i=>i.Id).ToArray(),v=>v.All(x=>x.Done));
Check(all.Length==6 && all.All(x=>x.Stage=="quality"),"every queued quality request completes at concurrency one");
Check(fake.MaxActive==1,"quality concurrency limit enforced");
var before=fake.FastCalls;engine.Exchange("session-a",inputs,[]);await Task.Delay(70);Check(before==fake.FastCalls,"transport retries are idempotent");
Check(fake.Contexts["msg 6"].Length==5,"bounded preceding context supplied");
engine.Exchange("session-a",[new Incoming("late","slow fast")],[]);
var earlyQuality=await Poll(engine,"session-a",["late"],v=>v[0].Stage=="quality");
var afterLateFast=await Poll(engine,"session-a",["late"],v=>v[0].Done);
Check(afterLateFast[0].Stage=="quality" && afterLateFast[0].Revision==earlyQuality[0].Revision,"late fast cannot overwrite quality");
engine.Exchange("session-a",[new Incoming("fail","quality failure")],[]);
var fallback=await Poll(engine,"session-a",["fail"],v=>v[0].Done);Check(fallback[0].Stage=="fast" && fallback[0].Text.Length>0,"quality failure preserves fast result");
engine.Exchange("session-a",[new Incoming("old","old session slow","","team")],[]);
engine.Exchange("session-b",[new Incoming("new","new session","","team")],[]);
var next=await Poll(engine,"session-b",["old","new"],v=>v.Any(x=>x.Id=="new"&&x.Done));
Check(next.Length==1 && next[0].Id=="new","session rollover discards stale results");
Check(fake.Contexts["new session"].Length==0,"session rollover clears context");
config.Save(config.Current with{Mode="fast"});engine.Exchange("session-b",[new Incoming("fast-only","only fast")],[]);
var fastOnly=await Poll(engine,"session-b",["fast-only"],v=>v[0].Done);Check(fastOnly[0].Stage=="fast","fast-only mode");
config.Save(config.Current with{Enabled=false});var disabled=engine.Exchange("session-b",[new Incoming("off","disabled")],[]);Check(disabled[0].Stage=="skipped" && disabled[0].Done,"disable stops translation requests");
config.Save(config.Current with{Enabled=true,Mode="quality"});engine.Exchange("session-b",[new Incoming("quality-only","only quality")],[]);
var qualityOnly=await Poll(engine,"session-b",["quality-only"],v=>v[0].Done);Check(qualityOnly[0].Stage=="quality","quality-only mode");
threw=false;try{ConfigStore.Validate(config.Current with{LlmBaseUrl="https://example.com/v1?key=secret"});}catch(ArgumentException){threw=true;}Check(threw,"URL credentials/query rejected");
int outgoingCalls=0;
await using var outgoing=new OutgoingService(config,async(text,settings,forceFast,ct)=>{Interlocked.Increment(ref outgoingCalls);await Task.Delay(30,ct);if(text=="fail")throw new InvalidOperationException();return "Wait for my ult";});
outgoing.Exchange("out-session",[new OutgoingInput("o1","等我大招","team")],[]);
outgoing.Exchange("out-session",[new OutgoingInput("o1","等我大招","team")],[]);
for(int i=0;i<100 && outgoing.Recent[0].State!="ready";i++)await Task.Delay(10);
Check(outgoingCalls==1 && outgoing.Recent[0].English=="Wait for my ult","outgoing retries do not duplicate translation");
Check(outgoing.Recent[0].Channel=="team","outgoing preserves source channel");
outgoing.Exchange("out-session",[],["o1"]);Check(outgoing.Recent[0].State=="submitted","outgoing acknowledgement prevents resend");
outgoing.Exchange("out-session",[new OutgoingInput("o2","fail","all")],[]);
for(int i=0;i<100 && outgoing.Recent.Last().State!="failed";i++)await Task.Delay(10);
Check(outgoing.Recent.Last().Original=="fail" && outgoing.Recent.Last().State=="failed","failed outgoing retains draft without sending");
var safe=OutgoingService.SafeMessage("hello\";quit\\x\nnext");Check(!safe.Any(c=>c=='"'||c==';'||c=='\\'||c=='\n'),"console command delimiters are neutralized");
outgoing.Exchange("next-session",[],[]);Check(outgoing.Recent.Length==0,"outgoing session rollover discards old tasks");

// ---- 0.2.2: receive scope, bilingual send text, channel waiting --------------------------
var scopeDir=Path.Combine(temp,"migrate");Directory.CreateDirectory(scopeDir);
File.WriteAllText(Path.Combine(scopeDir,"settings.json"),"{\"mode\":\"fast\",\"fontSize\":18,\"enabled\":false,\"translateOwn\":true}");
var migrated=new ConfigStore(scopeDir);
Check(migrated.Current.IncomingScope=="all_players","old configuration without a scope migrates to all players");
Check(migrated.Current.OutgoingDisplayMode=="translation","old configuration keeps translation-only sending");
Check(migrated.Current.Mode=="fast" && migrated.Current.FontSize==18 && !migrated.Current.Enabled,"migration keeps every other setting");
Check(File.ReadAllText(Path.Combine(scopeDir,"settings.json")).Contains("incomingScope"),"migrated configuration is written back once");
Check(new ConfigStore(temp).Current.IncomingScope=="all_players","a new configuration defaults to all players");
bool badScope=false;try{ConfigStore.Validate(migrated.Current with{IncomingScope="enemies"});}catch(ArgumentException){badScope=true;}Check(badScope,"invalid receive scope rejected");
bool badDisplay=false;try{ConfigStore.Validate(migrated.Current with{OutgoingDisplayMode="both"});}catch(ArgumentException){badDisplay=true;}Check(badDisplay,"invalid send content rejected");
Check(OutgoingService.BuildSendText("等我大招","Wait for my ult","translation")=="Wait for my ult","translation-only send text");
Check(OutgoingService.BuildSendText("等我大招","Wait for my ult","bilingual")=="等我大招 | Wait for my ult","bilingual send text keeps the original first");
Check(OutgoingService.BuildSendText("  Hello   world ","hello world","bilingual")=="hello world","whitespace-identical sides collapse to one copy");
Check(OutgoingService.BuildSendText("hello","HELLO","bilingual")=="HELLO","case-identical sides collapse to one copy");
Check(OutgoingService.BuildSendText(new string('x',250),new string('y',247),"bilingual").Length==500,"combined length counts the separator exactly");
bool overCombined=false;try{OutgoingService.BuildSendText(new string('x',250),new string('y',248),"bilingual");}catch(InvalidDataException){overCombined=true;}Check(overCombined,"one unit over the combined limit is refused");
Check(OutgoingService.NormalizeLine("a\nb\tc")=="a b c","newlines and tabs fold into single spaces");
bool tooLong=false;try{OutgoingService.BuildSendText(new string('x',600),"y","bilingual");}catch(InvalidDataException ex){tooLong=ex.Message.Contains("组合消息过长");}Check(tooLong,"over long combined message fails with the bilingual hint");
bool longEnglish=false;try{OutgoingService.BuildSendText("x",new string('y',501),"translation");}catch(InvalidDataException){longEnglish=true;}Check(longEnglish,"over long English is rejected without truncation");
var contextDir=Path.Combine(temp,"context");Directory.CreateDirectory(contextDir);
await using var contextEngine=new JobEngine(new ConfigStore(contextDir),fake);
contextEngine.Exchange("ctx",[new Incoming("c1","team line one","","team"),new Incoming("c2","all line one","","all"),new Incoming("c3","no channel line","","")],[]);
await Poll(contextEngine,"ctx",["c1","c2","c3"],v=>v.All(x=>x.Done));
Check(fake.Contexts["team line one"].Length==0,"first team line has no team context yet");
Check(fake.Contexts["all line one"].Length==0,"all chat does not inherit team context");
Check(fake.Contexts["no channel line"].Length==0,"an unknown channel uses only the current message");
contextEngine.Exchange("ctx",[new Incoming("c4","team line two","","team")],[]);
await Poll(contextEngine,"ctx",["c4"],v=>v.All(x=>x.Done));
Check(fake.Contexts["team line two"].Length==1 && fake.Contexts["team line two"][0]=="team line one","team context stays inside the team channel");
var sendDir=Path.Combine(temp,"send");Directory.CreateDirectory(sendDir);
await using var bilingual=new OutgoingService(new ConfigStore(sendDir),async(text,settings,forceFast,ct)=>{await Task.Delay(10,ct);return "Wait for my ult";});
bilingual.Exchange("s1",[new OutgoingInput("b1","等我大招","team","bilingual")],[]);
var bilingualView=await WaitOutgoing(bilingual,"b1");
Check(bilingualView.DisplayMode=="bilingual" && bilingualView.SendText=="等我大招 | Wait for my ult","bilingual task keeps the captured mode and combined text");
Check(bilingualView.English=="Wait for my ult","bilingual view keeps the English result separate");
bilingual.Exchange("s1",[],["b1"]);Check(bilingual.Recent[0].State=="submitted","bilingual task acknowledges once");
bilingual.Exchange("s1",[new OutgoingInput("t1","原文","team")],[]);
var translationView=await WaitOutgoing(bilingual,"t1");
Check(translationView.DisplayMode=="translation" && translationView.SendText=="Wait for my ult","a request without a display mode keeps the old behaviour");
bilingual.Exchange("s1",[],["t1"]);
bilingual.Exchange("s1",[new OutgoingInput("w1","等我大招","team")],[]);
await WaitOutgoing(bilingual,"w1");
bilingual.Exchange("s1",[],[],["w1"]);
Check(bilingual.Recent.First(x=>x.Id=="w1").State=="waiting_channel","an unconfirmed channel is reported, not downgraded to another channel");
bilingual.Exchange("s1",[],[],["w1"]);
Check(bilingual.Recent.First(x=>x.Id=="w1").State=="waiting_channel" && bilingual.Recent.First(x=>x.Id=="w1").Original=="等我大招","a waiting task keeps the original text");
bilingual.Exchange("s1",[],["w1"]);
Check(bilingual.Recent.First(x=>x.Id=="w1").State=="submitted" && bilingual.Recent.First(x=>x.Id=="w1").Error==null,"a later confirmed channel submits and clears the waiting reason");
bilingual.Exchange("s1",[new OutgoingInput("party1","你好","party","bilingual")],[]);
var partyView=await WaitOutgoing(bilingual,"party1");
Check(partyView.Channel=="party" && partyView.SendText=="你好 | Wait for my ult","PARTY is preserved separately from ALLIES and ALL in bilingual outgoing tasks");
bilingual.Exchange("s1",[],["party1"]);
bool badTask=false;try{bilingual.Exchange("s1",[new OutgoingInput("bad","x","team","both")],[]);}catch(ArgumentException){badTask=true;}Check(badTask,"invalid per task display mode rejected");
bilingual.Exchange("s1",[new OutgoingInput("r1","原文","all")],[]);
await WaitOutgoing(bilingual,"r1");
bilingual.Exchange("s1",[],[],[],[new OutgoingRefusal("r1","backend-outdated")]);
Check(bilingual.Recent.First(x=>x.Id=="r1").State=="failed" && bilingual.Recent.First(x=>x.Id=="r1").Error!.Contains("同步更新"),"a client refusal is surfaced with its reason");
bilingual.Exchange("s1",[new OutgoingInput("r2","原文","all")],[]);
await WaitOutgoing(bilingual,"r2");
bilingual.Exchange("s1",[],[],[],[new OutgoingRefusal("r2","something-unknown")]);
Check(bilingual.Recent.First(x=>x.Id=="r2").Error!.Contains("未提交"),"an unknown refusal reason still reports not sent");
var hotkeyNow=Environment.TickCount64;
long expiryClock=1000;
await using(var expiryService=new OutgoingService(config,(text,settings,fallback,token)=>Task.FromResult("Hello"),()=>expiryClock)){
    expiryService.Exchange("expiry",[new OutgoingInput("e1","test","team"),new OutgoingInput("e2","test","all"),new OutgoingInput("e3","test","team")],[]);
    await WaitOutgoing(expiryService,"e3","waiting_order");
    expiryClock+=100000;
    var admitted=expiryService.Exchange("expiry",[new OutgoingInput("e4","next","team")],[]);
    Check(admitted.First(x=>x.Id=="e4").State!="failed","expired results free queue slots before admitting a new message");
    await WaitOutgoing(expiryService,"e4");
    expiryClock+=100000;
    Check(expiryService.Recent.First(x=>x.Id=="e4").State=="failed","settings polling expires an unsent result even after the game bridge stops");
}
var startedCancel=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseCancel=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using(var cancellationService=new OutgoingService(config,async(text,settings,fallback,token)=>{
    startedCancel.TrySetResult();await releaseCancel.Task.WaitAsync(token);return "cancelled result";
})){
    cancellationService.Exchange("cancel-session",[new OutgoingInput("cancel1","pending","team")],[]);
    await startedCancel.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cancellationService.Exchange("cancel-session",[],[],[],[new OutgoingRefusal("cancel1","disabled")]);
    Check(cancellationService.Recent[0].State=="failed" && cancellationService.Recent[0].Error!.Contains("已取消"),"disable refusal cancels a translating task immediately");
    releaseCancel.TrySetResult();
}
await using(var stableCancel=new OutgoingService(config,async(text,settings,fallback,token)=>{await Task.Delay(20,token);return "result";})){
    stableCancel.Exchange("cancel-session",[new OutgoingInput("cancel2","pending","team")],[]);
    stableCancel.Exchange("cancel-session",[],[],[],[new OutgoingRefusal("cancel2","disabled")]);
    await Task.Delay(80);
    Check(stableCancel.Recent[0].State=="failed" && stableCancel.Recent[0].SendText=="","a cancelled queued task never returns to ready");
}
Check(!NativeHotkey.AllowsCapture(true,false,hotkeyNow,hotkeyNow),"the send chord is ignored while the chat is closed");
Check(NativeHotkey.AllowsCapture(true,true,hotkeyNow,hotkeyNow-3000),"a chat reported open three seconds ago still captures");
Check(!NativeHotkey.AllowsCapture(true,true,hotkeyNow,hotkeyNow-6000),"a stale chat report stops capturing");
Check(!NativeHotkey.AllowsCapture(false,true,hotkeyNow,hotkeyNow),"disabling send translation stops capturing");
// Local fake Chat Completions service: verifies the outgoing English path without any real key.
var fakeDir=Path.Combine(temp,"fake-llm");Directory.CreateDirectory(fakeDir);
var fakeConfig=new ConfigStore(fakeDir);
fakeConfig.Save(fakeConfig.Current with{Mode="quality",OutgoingMode="quality",LlmBaseUrl="http://127.0.0.1:17870/v1",Model="fake-local",LlmKey="local-test-key"});
using var fakeHttp=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
var fakeTranslators=new Translators(fakeConfig,new Terms(fakeDir,Path.Combine(root,"src/Bridge/data/terms.json")),fakeHttp);
bool fakeUp=false;try{using var probe=await fakeHttp.GetAsync("http://127.0.0.1:17870/none");fakeUp=probe.StatusCode==System.Net.HttpStatusCode.NotFound;}catch{fakeUp=false;}
if(fakeUp){
    var english=await fakeTranslators.ToEnglish("等我大招，别打。",fakeConfig.Current,false,CancellationToken.None);
    Check(english=="Wait for my ultimate, don't attack.","outgoing English reaches the configured Chat Completions service");
    var composed=OutgoingService.BuildSendText("等我大招，别打。",english,"bilingual");
    Check(composed=="等我大招，别打。 | "+english,"composed bilingual send text uses the service result");
}else Console.WriteLine("SKIP: local fake Chat Completions service is not running on 17870");


File.WriteAllText(Path.Combine(root,"artifacts/core-tests.json"),JsonSerializer.Serialize(new{passed,at=DateTime.UtcNow},ConfigStore.Json));Console.WriteLine($"TOTAL {passed} passed");
static async Task<JobView[]> Poll(JobEngine engine,string session,string[] ids,Func<JobView[],bool> done){for(int i=0;i<500;i++){var v=engine.Exchange(session,[],ids);if(v.Length>0&&done(v))return v;await Task.Delay(10);}throw new TimeoutException("jobs did not complete");}
static async Task<OutgoingView> WaitOutgoing(OutgoingService outgoing,string id,string state="ready"){for(int i=0;i<300;i++){var v=outgoing.Recent.FirstOrDefault(x=>x.Id==id);if(v!=null && v.State==state)return v;await Task.Delay(10);}throw new TimeoutException("outgoing job "+id+" never reached "+state);}
sealed class CaptureHttp:HttpMessageHandler {
    public string? Body;public string Reply="";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
        Body=await request.Content!.ReadAsStringAsync(token);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=Reply}}}}))};
    }
}
sealed class Fake:ITranslator {
    public int FastCalls,Active,MaxActive;public System.Collections.Concurrent.ConcurrentDictionary<string,string[]> Contexts=new();
    public async Task<string> Fast(string text,Settings c,CancellationToken ct){Interlocked.Increment(ref FastCalls);await Task.Delay(text=="slow fast"?250:10,ct);return "快译 "+text;}
    public async Task<string> Quality(string text,string[] context,Settings c,CancellationToken ct){var active=Interlocked.Increment(ref Active);MaxActive=Math.Max(MaxActive,active);Contexts[text]=context;try{await Task.Delay(text=="old session slow"?1000:70,ct);if(text=="quality failure")throw new InvalidOperationException("模拟失败");return "精翻 "+text;}finally{Interlocked.Decrement(ref Active);}}
}
