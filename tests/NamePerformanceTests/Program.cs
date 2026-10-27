using DeadlockTranslate;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var baseline=args.Contains("--baseline");
var temp=Path.Combine(root,"artifacts/name-performance",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var terms=new Terms(temp,Path.Combine(root,"src/Bridge/data/terms.json"));var passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
var cases=new[]{
    "蓝牛没大招，狼女在后面，先别打，等10秒",
    "蓝牛、亚伯兰、亚伯拉姆斯，狼女、希尔芙、西尔芙，老七、柒，神鞭、劳什没大招；买水银装填、魔力装填、金属皮肤、铜皮铁骨，等10秒再打",
    string.Join("，",terms.All.Where(t=>t.Kind=="name"&&!t.Ambiguous&&t.Source==t.English).Take(24).Select(t=>"买"+t.Target))+"，不要现在开团，等15秒"
};
var metrics=new List<object>();
var store=new ConfigStore(Path.Combine(temp,"provider"));store.Save(store.Current with{LlmKey="fake-local-key",LlmBaseUrl="https://fixture.invalid/v1",OutgoingMode="quality"});
using var fixture=new Fixture();using var http=new HttpClient(fixture);using var translator=new Translators(store,terms,http);
foreach(var source in cases){
    for(var i=0;i<20;i++){var p=terms.ProtectOutgoing(source);Terms.Restore(p.Text,p.Map);}
    var timings=new List<double>();
    for(var i=0;i<200;i++){var timer=Stopwatch.StartNew();var p=terms.ProtectOutgoing(source);Terms.Restore(p.Text,p.Map);timings.Add(timer.Elapsed.TotalMilliseconds);}
    timings.Sort();var result=await translator.TranslateOutgoing(source,store.Current,false,CancellationToken.None);
    using var payload=JsonDocument.Parse(fixture.LastInput!);var input=payload.RootElement;
    Check(result==input.GetProperty("current").GetString(),"mock translation retains canonical names, negation and numbers");
    metrics.Add(new{sourceCharacters=source.Length,protectedOccurrences=terms.ProtectOutgoing(source).Map.Count,prepareP50Ms=timings[100],prepareP95Ms=timings[190],modelInputCharacters=fixture.LastInput!.Length,glossaryEntries=input.GetProperty("glossary").GetArrayLength(),requiredNames=input.GetProperty("requiredNames").GetArrayLength(),systemCharacters=fixture.SystemCharacters});
}
if(!baseline){
    var before=fixture.Calls;
    Check(await translator.TranslateOutgoing("蓝牛、狼女、神鞭",store.Current,false,CancellationToken.None)=="Abrams, Silver, Lash"&&fixture.Calls==before,"name-only lists use local canonical names without a cloud request");
    Check(await translator.TranslateOutgoing("蓝牛，狼女",store.Current with{OutgoingTargetLanguage="ja"},false,CancellationToken.None)=="Abrams，Silver"&&fixture.Calls==before,"non-English name lists keep canonical names and punctuation");
    foreach(var source in new[]{"蓝牛和狼女","蓝牛没大招","蓝牛不要打","蓝牛 10秒","蓝牛 low hp","wait seven seconds","my pocket is empty"}){
        Check(TranslationPrompt.NameList(terms.PrepareOutgoingNames(source))==null,"tactical/ordinary text cannot take name-only shortcut: "+source);
        await translator.TranslateOutgoing(source,store.Current,false,CancellationToken.None);
    }
    Check(fixture.Calls==before+7,"every partial tactical sentence still uses the model");
    var uncertain=terms.PrepareOutgoingNames("Seven has no ult, wait seven seconds; my pocket is empty");
    Check(uncertain.Map.Values.SequenceEqual(new[]{"Seven"}),"local ambiguity is evaluated per occurrence");
    Check(uncertain.Hints.Any(t=>t.Source.Equals("Seven",StringComparison.OrdinalIgnoreCase))&&uncertain.Hints.Any(t=>t.Source.Equals("Pocket",StringComparison.OrdinalIgnoreCase)),"unresolved words retain disambiguation hints even with a resolved occurrence");
    using(var input=JsonDocument.Parse(TranslationPrompt.Build(Terms.Restore(uncertain.Text,uncertain.Map),uncertain.Map.Values,uncertain.Hints,TranslationLanguages.Presets[0],["no ult", "wait 10 seconds"]))){
        Check(input.RootElement.GetProperty("context").GetArrayLength()==2,"receive context survives prompt compaction");
        Check(input.RootElement.GetProperty("glossary").EnumerateArray().Any(t=>t.GetProperty("ambiguous").GetBoolean()),"ambiguous hints retain their flag");
    }
    var slots=new Dictionary<string,string>{{"DLTERM0X","Abrams"},{"DLTERM10X","Silver"}};
    Check(Terms.Restore("dl term 10 x and DL TERM 0 X",slots)=="Silver and Abrams","one-pass restore tolerates case and spaces without 0/10 confusion");
    Check(Terms.Restore("DLTERM0X DLTERM0X DLTERM10X",slots)=="Abrams Abrams Silver","one-pass restore handles repeated occurrences");
    Check(Terms.Restore("DLTERM0X DLTERM1X",new(){{"DLTERM0X","DLTERM1X"},{"DLTERM1X","Silver"}})=="DLTERM1X Silver","replacement values are not recursively interpreted as tokens");
    try{Terms.Restore("DLTERM0X",slots);throw new Exception("expected missing token");}catch(InvalidDataException){Check(true,"missing required placeholder is rejected");}
    var literal=terms.PrepareOutgoingNames("DLTERM0X 集火蓝牛");
    Check(Terms.Restore(literal.Text,literal.Map)=="DLTERM0X 集火Abrams","user literal tokens cannot collide with generated tokens");
    var owned=new Terms(Path.Combine(temp,"owned"),Path.Combine(temp,"absent"));Directory.CreateDirectory(Path.Combine(temp,"owned"));
    var edits=new[]{new Term("alias","新名","New")};owned.Save(edits);edits[0]=new("alias","坏名","Bad");
    Check(owned.Exact("alias",false)=="新名","saving owns an immutable copy of caller's array");
    var copy=owned.All;copy[0]=new("alias","坏名","Bad");Check(owned.Exact("alias",false)=="新名","exported array cannot mutate internal glossary");
    var oldPrepared=owned.PrepareOutgoingNames("alias is low");owned.Save([new("alias","更名","Changed")]);
    Check(oldPrepared.Revision==1&&owned.Revision==2&&Terms.Restore(oldPrepared.Text,oldPrepared.Map)=="New is low","prepared data remains tied to its original glossary revision");
    Check(owned.ProtectOutgoing("alias is low").Map.Values.Single()=="Changed","saving rebuilds exact/match indexes");
    owned.Save(Enumerable.Range(0,5000).Select(i=>new Term("unit"+i.ToString("D5"),"单位"+i,"Unit"+i)).ToArray());
    Check(owned.Match("unit04999 is low").Single().English=="Unit4999","maximum-size glossary remains searchable");
    var prepared=owned.PrepareOutgoingNames("unit04999 is low");Check(prepared.Map.Values.Single()=="Unit4999","maximum-size snapshot protects the last entry");
    await using var log=new DiagnosticLog(Path.Combine(temp,"timing"));
    var serialConfig=new ConfigStore(Path.Combine(temp,"serial"));serialConfig.Save(serialConfig.Current with{LlmKey="fake-local-key",LlmBaseUrl="https://fixture.invalid/v1",OutgoingMode="quality",LlmConcurrency=1});
    using var delayed=new Fixture{HoldFirst=true};using var delayedHttp=new HttpClient(delayed);using var serial=new Translators(serialConfig,terms,delayedHttp,log);
    var first=serial.TranslateOutgoing("蓝牛没大招",serialConfig.Current,false,CancellationToken.None);await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second=serial.TranslateOutgoing("狼女没大招",serialConfig.Current,false,CancellationToken.None);
    await Task.Delay(60);Check(delayed.Calls==1&&!second.IsCompleted,"compacted requests still obey shared concurrency");
    delayed.Release.TrySetResult();await Task.WhenAll(first,second);await log.FlushAsync();
    var raw=string.Concat((await log.SnapshotAsync()).Select(bytes=>System.Text.Encoding.UTF8.GetString(bytes)));var timing=raw.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>JsonSerializer.Deserialize<SafeLogEvent>(l,new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}})!).ToArray();
    Check(timing.Count(e=>e.Event==LogKind.TranslationTiming)==6,"each uncached cloud request records preparation, queue and response timings");
    Check(timing.Where(e=>e.Event==LogKind.TranslationTiming).GroupBy(e=>e.Data.Token).Count()==2&&timing.Where(e=>e.Event==LogKind.TranslationTiming).GroupBy(e=>e.Data.Token).All(g=>g.Key?.Length==16&&g.Count()==3),"overlapping request stages share one opaque correlation token per request");
    Check(timing.Any(e=>e.Data.Stage=="provider-queue"&&e.Data.ElapsedMs>=40)&&timing.Any(e=>e.Data.Stage=="provider-response"&&e.Data.ElapsedMs>=40),"queue waiting and provider latency are measured separately");
    Check(!raw.Contains("Abrams")&&!raw.Contains("蓝牛")&&!raw.Contains("fake-local-key")&&!raw.Contains("fixture.invalid"),"timing log contains no chat, names, address or key");
    var calls=delayed.Calls;await serial.TranslateOutgoing("蓝牛没大招",serialConfig.Current,false,CancellationToken.None);Check(delayed.Calls==calls,"existing quality cache avoids a second HTTP call");
    using var stopped=new CancellationTokenSource();stopped.Cancel();try{await serial.TranslateOutgoing("蓝牛、狼女",serialConfig.Current,false,stopped.Token);throw new Exception("expected cancellation");}catch(OperationCanceledException){Check(true,"local shortcut honors cancellation");}
}
File.WriteAllText(Path.Combine(root,"artifacts",baseline?"name-performance-before.json":"name-performance-after.json"),JsonSerializer.Serialize(new{passed,metrics,scope="real lexicon, synthetic messages and in-memory HTTP; no cloud latency, game FPS or human quality measurement",at=DateTime.UtcNow},ConfigStore.Json));
Console.WriteLine(JsonSerializer.Serialize(metrics,ConfigStore.Json));
Console.WriteLine($"TOTAL {passed} passed");

sealed class Fixture:HttpMessageHandler{
    public string? LastInput;public int SystemCharacters;public int Calls;public bool HoldFirst;
    public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        var call=Interlocked.Increment(ref Calls);if(HoldFirst&&call==1){Entered.TrySetResult();await Release.Task.WaitAsync(ct);}using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var messages=body.RootElement.GetProperty("messages");
        SystemCharacters=messages[0].GetProperty("content").GetString()!.Length;LastInput=messages[1].GetProperty("content").GetString();
        using var input=JsonDocument.Parse(LastInput!);return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason="stop",message=new{content=input.RootElement.GetProperty("current").GetString()}}}})};
    }
}
