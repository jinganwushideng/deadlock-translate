using DeadlockTranslate;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var source="阿波罗出个跑酷，用来应对减速魔咒";
var isolated=Path.Combine(root,"artifacts/send-terms",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolated);
var defaults=Path.Combine(root,"src/Bridge/data/terms.json");
var terms=new Terms(isolated,defaults);var prepared=terms.PrepareOutgoingNames(source);
Console.WriteLine(JsonSerializer.Serialize(new{canonicalInput=Terms.Restore(prepared.Text,prepared.Map),requiredNames=prepared.Map.Values.Distinct(),hints=prepared.Hints.Select(t=>t.Source)},ConfigStore.Json));
if(args.Contains("--live")){
    // Explicit opt-in uses only this synthetic user-reported sentence. No game, chat
    // history, configuration edits, raw provider body or credential is exported.
    var state=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate");
    using(var configJson=JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"settings.json")))){
        if(!configJson.RootElement.TryGetProperty("incomingScope",out _))throw new InvalidOperationException("Refuse to migrate personal configuration in a test");
    }
    var config=new ConfigStore(state);var personal=new Terms(state,defaults);var selected=args.Contains("--defaults")?terms:personal;
    var selectedPreparation=selected.PrepareOutgoingNames(source);
    using var capture=new Capture{InnerHandler=new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All}};
    using var http=new HttpClient(capture){Timeout=Timeout.InfiniteTimeSpan,MaxResponseContentBufferSize=2*1024*1024};using var translator=new Translators(config,selected,http);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));var timer=Stopwatch.StartNew();string? failure=null;bool translated=false;
    try{await translator.TranslateOutgoing(source,config.Current with{OutgoingMode="quality"},false,timeout.Token);translated=true;}catch(Exception ex){failure=ex.GetType().Name;}
    var missing=selectedPreparation.Map.Values.Distinct().Where(n=>!Regex.IsMatch(capture.Reply??"",Terms.Pattern(n),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))).ToArray();
    var report=new{translated,failure,requestedTokens=capture.RequestedTokens,thinkingOverride=capture.ThinkingOverride,httpStatus=capture.Status,finishReason=capture.Finish,replyCharacters=capture.Reply?.Length??0,missingNames=missing,completionTokens=capture.CompletionTokens,reasoningTokens=capture.ReasoningTokens,elapsedMs=timer.ElapsedMilliseconds,scope="one synthetic real-cloud request; personal configuration not modified; raw reply/URL/key excluded"};
    File.WriteAllText(Path.Combine(root,"artifacts",args.Contains("--after")?"send-term-live-after.json":"send-term-live-before.json"),JsonSerializer.Serialize(report,ConfigStore.Json));Console.WriteLine(JsonSerializer.Serialize(report,ConfigStore.Json));return;
}
var passed=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
Check(prepared.Map.Values.SequenceEqual(new[]{"Apollo","Stamina Mastery","Slowing Hex"}),"reported sentence resolves hero, item shorthand and hex canonically");
Check(Terms.Restore(prepared.Text,prepared.Map)=="Apollo出个Stamina Mastery，用来应对Slowing Hex","reported sentence retains its purchase and counterplay intent");
foreach(var s in new[]{"买个跑酷","出一个跑酷","出一件跑酷装","补个跑酷","购买跑酷大师"})Check(terms.PrepareOutgoingNames(s).Map.Values.Contains("Stamina Mastery"),"purchase shorthand is protected: "+s);
foreach(var s in new[]{"我在练跑酷","喜欢跑酷运动","我们玩跑酷游戏"})Check(terms.ProtectOutgoing(s).Map.Count==0,"ordinary parkour stays ordinary: "+s);
Check(new Settings().LlmMaxTokens==4096,"old configuration gets a moderately larger 4096 budget");
foreach(var limit in new[]{0,511,16385}){try{ConfigStore.Validate(new Settings{LlmMaxTokens=limit});throw new Exception("expected validation");}catch(ArgumentException){Check(true,"invalid token budget rejected: "+limit);}}
foreach(var limit in new[]{512,2048,4096,6144,16384}){
    using var content=LlmRequestOptions.Create("fixture","system","input",limit);using var body=JsonDocument.Parse(await content.ReadAsStringAsync());
    Check(body.RootElement.GetProperty("max_tokens").GetInt32()==limit&&!body.RootElement.TryGetProperty("thinking",out _)&&!body.RootElement.TryGetProperty("reasoning_effort",out _),"selected budget preserves provider thinking without overrides: "+limit);
}
var cfg=new ConfigStore(Path.Combine(isolated,"provider"));cfg.Save(cfg.Current with{LlmKey="fake-local-key",LlmBaseUrl="https://api.deepseek.com/v1",Model="deepseek-flash",OutgoingMode="quality"});
using var mock=new Mock();using var mockHttp=new HttpClient(mock);using var service=new Translators(cfg,terms,mockHttp);
var result=await service.TranslateOutgoing(source,cfg.Current,false,CancellationToken.None);
Check(result=="Apollo, buy Stamina Mastery to counter Slowing Hex.","reported sentence passes quality normalization");
Check(mock.LastTokens==4096&&mock.ThinkingOverride,"outgoing quality explicitly preserves thinking on official API");
Check(terms.NormalizeOutgoingQualityNames("Apollo, buy Stamina-Mastery to counter Slowing_Hex.",prepared.Map)=="Apollo, buy Stamina Mastery to counter Slowing Hex.","hyphenated or underscored name formatting is normalized without a cloud retry");
Check(terms.NormalizeOutgoingQualityNames("阿波罗，出跑酷大师应对减速魔咒",prepared.Map).Contains("Slowing Hex"),"known unambiguous Chinese name aliases are repaired locally");
Check(terms.PrepareOutgoingNames("能不能出个减速魔咒针对下阿波罗").Map.Values.SequenceEqual(new[]{"Slowing Hex","Apollo"}),"latest reported purchase/counterplay sentence resolves exactly the two explicit names");
foreach(var bad in new[]{"buy Slow Hex to counter Apollo with Stamina Mastery","buy Stamina Mastery to counter him"}){
 try{terms.NormalizeOutgoingQualityNames(bad,prepared.Map);throw new Exception("expected missing name");}catch(InvalidDataException){Check(true,"local normalization never invents a missing item or replaces a pronoun with a guessed hero");}
}
var repairCalls=mock.Calls;mock.MissingOnce=true;
result=await service.TranslateOutgoing(source+"！修复",cfg.Current,false,CancellationToken.None);
Check(result.Contains("Slowing Hex")&&mock.Calls==repairCalls+2&&mock.RepairPromptSeen,"a missing-name answer is regenerated once from the original with explicit required names");
Check(mock.LastTokens==4096&&mock.ThinkingOverride,"name repair preserves the chosen token budget and thinking mode");
repairCalls=mock.Calls;await service.TranslateOutgoing(source+"！修复",cfg.Current,false,CancellationToken.None);Check(mock.Calls==repairCalls,"a repaired translation is cached only after complete name validation");
await service.Quality("Apollo buy Stamina Mastery",[],cfg.Current with{IncomingTargetLanguage="en"},CancellationToken.None);
Check(mock.LastTokens==4096&&mock.ThinkingOverride,"incoming quality uses the same budget and thinking control");
var calls=mock.Calls;await service.TranslateOutgoing(source,cfg.Current with{LlmMaxTokens=6144},false,CancellationToken.None);
Check(mock.Calls==calls+1&&mock.LastTokens==6144&&mock.ThinkingOverride,"different token budgets cannot share a cached translation");
var apiResult=await service.TestApiConfiguration(new("llm",MaxTokens:4096));Check(apiResult.Outcome=="ok"&&mock.LastTokens==4096&&mock.ThinkingOverride,"connection test respects unsaved token budget");
apiResult=await service.TestApiConfiguration(new("llm",MaxTokens:6144));Check(apiResult.Outcome=="ok"&&mock.LastTokens==6144&&mock.ThinkingOverride,"connection test uses explicit budget and official thinking parameter");
Check(!ApiConfigurationDiagnostics.Inspect(cfg,new("llm",MaxTokens:0)).Ready,"invalid budget draft is blocked locally");
mock.ReasoningOnly=true;
try{await service.TranslateOutgoing(source+"！",cfg.Current,false,CancellationToken.None);throw new Exception("expected reasoning failure");}catch(TranslationOutputException ex){Check(ex.Reason==LogReason.ReasoningBudget&&ex.Message.Contains("思考")&&!ex.Message.Contains("PRIVATE_REASONING"),"reasoning budget exhaustion has safe, specific guidance");}
apiResult=await service.TestApiConfiguration(new("llm"));Check(apiResult.Code=="reasoning-budget"&&apiResult.Field=="tokens","connection test classifies thinking-budget exhaustion too");
await using var log=new DiagnosticLog(Path.Combine(isolated,"logs"));
await using(var outgoing=new OutgoingService(cfg,service.TranslateOutgoing,log:log)){
    outgoing.Exchange("fixture-session",[new("task",source+"！！","team")],[]);
    var deadline=Environment.TickCount64+3000;while(outgoing.Recent.Single().State!="failed"&&Environment.TickCount64<deadline)await Task.Delay(10);
    Check(outgoing.Recent.Single().State=="failed"&&outgoing.Recent.Single().Error?.Contains("思考耗尽")==true&&outgoing.Diagnostics.FailureCode=="ReasoningBudget","recent sending shows the real cause and retains failed state");
    await log.FlushAsync();var safe=string.Concat((await log.SnapshotAsync()).Select(b=>System.Text.Encoding.UTF8.GetString(b)));
    Check(safe.Contains("ReasoningBudget")&&safe.Contains("TranslationOutputException")&&!safe.Contains("PRIVATE_REASONING")&&!safe.Contains("阿波罗")&&!safe.Contains("fake-local-key"),"sending log exports category/type without chat, reasoning or key");
}
mock.ReasoningOnly=false;mock.Truncated=true;
try{await service.TranslateOutgoing(source+"？",cfg.Current,false,CancellationToken.None);throw new Exception("expected truncation");}catch(TranslationOutputException ex){Check(ex.Reason==LogReason.OutputTruncated,"partial answer is never accepted when finish reason is length");}
mock.Truncated=false;mock.MissingName=true;
var beforeMissing=mock.Calls;
try{await service.TranslateOutgoing(source+"。",cfg.Current,false,CancellationToken.None);throw new Exception("expected name failure");}catch(TranslationOutputException ex){Check(ex.Reason==LogReason.NameMismatch,"required-name rejection stays distinct from output exhaustion");}
Check(mock.Calls==beforeMissing+2,"persistent missing names stop after one repair attempt");
File.WriteAllText(Path.Combine(root,"artifacts/send-term-tests.json"),JsonSerializer.Serialize(new{passed,realApi=false,at=DateTime.UtcNow},ConfigStore.Json));Console.WriteLine("TOTAL "+passed+" passed");

sealed class Capture:DelegatingHandler{
    public string? Reply,Finish;public int Status,CompletionTokens,ReasoningTokens,RequestedTokens;public bool ThinkingOverride;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        using(var input=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct))){RequestedTokens=input.RootElement.GetProperty("max_tokens").GetInt32();ThinkingOverride=input.RootElement.TryGetProperty("thinking",out _)||input.RootElement.TryGetProperty("reasoning_effort",out _);}
        var response=await base.SendAsync(request,ct);Status=(int)response.StatusCode;
        if(response.IsSuccessStatusCode){using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var choice=body.RootElement.GetProperty("choices")[0];Finish=choice.TryGetProperty("finish_reason",out var f)?f.GetString():null;Reply=choice.GetProperty("message").GetProperty("content").GetString();
            if(body.RootElement.TryGetProperty("usage",out var usage)){if(usage.TryGetProperty("completion_tokens",out var tokens))CompletionTokens=tokens.GetInt32();if(usage.TryGetProperty("completion_tokens_details",out var details)&&details.TryGetProperty("reasoning_tokens",out var reasoning))ReasoningTokens=reasoning.GetInt32();}
        }
        return response;
    }
}

sealed class Mock:HttpMessageHandler{
    public int Calls,LastTokens;public bool ThinkingOverride,ReasoningOnly,Truncated,MissingName,MissingOnce,RepairPromptSeen;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        Calls++;using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));LastTokens=json.RootElement.GetProperty("max_tokens").GetInt32();ThinkingOverride=json.RootElement.TryGetProperty("thinking",out _)||json.RootElement.TryGetProperty("reasoning_effort",out _);
        var system=json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        RepairPromptSeen|=system!.Contains("A prior attempt omitted a required name");
        var missing=MissingName||MissingOnce;MissingOnce=false;
        var reply=system.Contains("connection diagnostic")?"OK":missing?"buy an item": "Apollo, buy Stamina Mastery to counter Slowing Hex.";
        return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason=ReasoningOnly||Truncated?"length":"stop",message=new{content=ReasoningOnly?"":reply,reasoning_content=ReasoningOnly?"PRIVATE_REASONING":""}}},usage=new{completion_tokens=ReasoningOnly?2048:13,completion_tokens_details=new{reasoning_tokens=ReasoningOnly?2048:0}}})};
    }
}
