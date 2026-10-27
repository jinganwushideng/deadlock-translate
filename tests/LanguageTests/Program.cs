using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts/language-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
int passed=0;
void Check(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);passed++;Console.WriteLine("PASS: "+label);}
void Invalid(Action action,string label){try{action();throw new Exception("invalid configuration accepted");}catch(ArgumentException){Check(true,label);}}
var settings=new Settings();Check(settings.IncomingTargetLanguage=="zh-Hans"&&settings.OutgoingTargetLanguage=="en","new defaults remain Chinese reception and English sending");
var oldFolder=Path.Combine(folder,"old");Directory.CreateDirectory(oldFolder);File.WriteAllText(Path.Combine(oldFolder,"settings.json"),"{\"incomingScope\":\"all_players\",\"fontSize\":19,\"llmBaseUrl\":\"https://fixture.invalid/v1\",\"model\":\"old-model\"}");
var old=new ConfigStore(oldFolder);Check(old.Current.IncomingTargetLanguage=="zh-Hans"&&old.Current.OutgoingTargetLanguage=="en"&&old.Current.FontSize==19&&old.Current.Model=="old-model","existing config retains all prior fields and gets language defaults");
Invalid(()=>ConfigStore.Validate(settings with{IncomingTargetLanguage="invalid"}),"unknown receive language rejected");
Invalid(()=>ConfigStore.Validate(settings with{OutgoingTargetLanguage="custom"}),"custom language requires a name");
Invalid(()=>ConfigStore.Validate(settings with{OutgoingTargetLanguage="custom",OutgoingCustomLanguage="荷兰语",OutgoingCustomLanguageCode="nl&to=en"}),"invalid fast language code rejected");
Invalid(()=>ConfigStore.Validate(settings with{IncomingTargetLanguage="custom",IncomingCustomLanguage="bad\nname"}),"custom language cannot carry control characters");
Invalid(()=>ConfigStore.Validate(settings with{IncomingTargetLanguage="custom",IncomingCustomLanguage="荷兰语",IncomingCustomLanguageCode="nl\n"}),"custom fast code rejects a trailing newline");
Invalid(()=>ConfigStore.Validate(settings with{IncomingTargetLanguage="custom",IncomingCustomLanguageCode=null!}),"null language fields produce validation errors");
var config=new ConfigStore(Path.Combine(folder,"config"));config.Save(config.Current with{LlmBaseUrl="https://fixture.invalid/v1",LlmKey="fake-test-key",AzureKey="fake-azure-key",Model="fixture",FastProvider="azure",OutgoingMode="fast"});
var terms=new Terms(folder,Path.Combine(folder,"no-defaults.json"));terms.Save([new("ult","大招","", "phrase"),new("Seven","老七","Seven"),new("qsr","魔力装填","Quicksilver Reload"),new("пушка","炮台","Turret")]);
using var net=new Fixture();using var http=new HttpClient(net);using var translator=new Translators(config,terms,http);
foreach(var language in TranslationLanguages.Presets){
 var c=config.Current with{IncomingTargetLanguage=language.Id,OutgoingTargetLanguage=language.Id};
 var translated=await translator.Fast("hello tactical chat",c,CancellationToken.None);Check(net.Targets.Last()==language.ServiceCode,"Azure receive target "+language.Id);
 var sent=await translator.TranslateOutgoing("测试消息",c,true,CancellationToken.None);Check(net.Targets.Last()==language.ServiceCode,"Azure send target "+language.Id);
}
var before=net.Calls;var czh=config.Current with{IncomingTargetLanguage="zh-Hans"};Check(await translator.Fast("ult",czh,CancellationToken.None)=="大招"&&net.Calls==before,"default Chinese exact phrases still avoid network");
var cja=config.Current with{IncomingTargetLanguage="ja"};var ja=await translator.Fast("ult",cja,CancellationToken.None);Check(net.Calls==before+1&&ja.StartsWith("ja:"),"non-Chinese exact phrase is translated, not forced to Chinese");
var cnames=config.Current with{IncomingTargetLanguage="ru"};var name=await translator.Fast("buy qsr now",cnames,CancellationToken.None);Check(name.Contains("Quicksilver Reload")&&!name.Contains("魔力"),"non-Chinese fast result preserves English canonical names");
Check(await translator.Fast("Seven",cnames,CancellationToken.None)=="Seven","unambiguous exact game name can reuse canonical English");
var quality=config.Current with{IncomingTargetLanguage="ja"};var result=await translator.Quality("Seven has no ult",[],quality,CancellationToken.None);Check(result.StartsWith("ja:")&&result.Contains("Seven")&&!result.Contains("老七"),"LLM receive prompt and protected names respect target language");
await translator.Quality("ult",[],quality with{IncomingTargetLanguage="zh-Hant"},CancellationToken.None);Check(net.Languages.Last().Name=="繁体中文","traditional Chinese goes through target-aware translation");
foreach(var language in TranslationLanguages.Presets){await translator.Quality("dynamic incoming target",[],config.Current with{IncomingTargetLanguage=language.Id},CancellationToken.None);Check(net.Languages.Last().Id==language.Id,"LLM receive target "+language.Id);}
var calls=net.Calls;await translator.Quality("same arbitrary line",[],quality,CancellationToken.None);await translator.Quality("same arbitrary line",[],quality,CancellationToken.None);Check(net.Calls==calls+1,"identical language requests still use cache");
await translator.Quality("same arbitrary line",[],quality with{IncomingTargetLanguage="ko"},CancellationToken.None);Check(net.Calls==calls+2,"language changes cannot reuse old quality cache");
var custom=config.Current with{IncomingTargetLanguage="custom",IncomingCustomLanguage="荷兰语",IncomingCustomLanguageCode="",OutgoingTargetLanguage="custom",OutgoingCustomLanguage="荷兰语",OutgoingCustomLanguageCode="",OutgoingMode="quality"};
await translator.Quality("custom reception",[],custom,CancellationToken.None);await translator.TranslateOutgoing("自定义发送",custom,false,CancellationToken.None);Check(net.Languages.TakeLast(2).All(l=>l.Name=="荷兰语"&&l.Code==null),"custom LLM targets work without a fast language code");
before=net.Calls;try{await translator.Fast("custom reception",custom,CancellationToken.None);throw new Exception("expected no fast code");}catch(InvalidOperationException e){Check(e.Message.Contains("语言代码")&&net.Calls==before,"missing fast code fails clearly without paid fallback");}
try{await translator.TranslateOutgoing("自定义发送",custom,true,CancellationToken.None);throw new Exception("expected no fast code");}catch(InvalidOperationException e){Check(e.Message.Contains("语言代码")&&net.Calls==before,"send fast fallback requires a code too");}
await translator.Fast("custom reception",custom with{IncomingCustomLanguageCode="nl"},CancellationToken.None);Check(net.Targets.Last()=="nl","custom fast service code routes correctly");
foreach(var language in TranslationLanguages.Presets){var send=config.Current with{OutgoingMode="quality",OutgoingTargetLanguage=language.Id};await translator.TranslateOutgoing("输出到不同语言",send,false,CancellationToken.None);Check(net.Languages.Last().Id==language.Id,"LLM send target "+language.Id);}
await translator.Fast("bing reception",config.Current with{FastProvider="bing",IncomingTargetLanguage="ru"},CancellationToken.None);Check(net.Targets.Last()=="ru","Bing receive uses selected target code");
await translator.TranslateOutgoing("必应发送",config.Current with{FastProvider="bing",OutgoingTargetLanguage="ko"},true,CancellationToken.None);Check(net.Targets.Last()=="ko","Bing send uses selected target code");

var queueConfig=new ConfigStore(Path.Combine(folder,"queue"));queueConfig.Save(queueConfig.Current with{OutgoingConcurrency=1,OutgoingMode="quality"});
var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var captured=new List<string>();
async Task<string> Send(string text,Settings c,bool fast,CancellationToken ct){lock(captured)captured.Add(c.OutgoingTargetLanguage);if(text=="first"){started.TrySetResult();await release.Task.WaitAsync(ct);}return "translated";}
await using(var outgoing=new OutgoingService(queueConfig,Send)){
 outgoing.Exchange("session",[new("one","first","team")],[]);await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
 outgoing.Exchange("session",[new("two","second","all","bilingual","ru")],[]);
 queueConfig.Save(queueConfig.Current with{OutgoingTargetLanguage="ko"});release.TrySetResult();
 for(int i=0;i<100&&outgoing.Recent.Any(v=>v.State is "queued" or "translating");i++)await Task.Delay(10);
 Check(captured.SequenceEqual(new[]{"en","ru"}),"pending sends keep captured language when settings change");
 var second=outgoing.Recent.Single(v=>v.Id=="two");Check(second.TargetLanguage=="ru"&&second.TargetLanguageName=="俄语"&&second.Translation==second.English&&second.SendText=="second | translated","multilingual bilingual view exposes generic translation and preserves channel/content");
 try{outgoing.Exchange("session",[new("bad","text","team","translation","custom","",null)],[]);throw new Exception("invalid captured language");}catch(ArgumentException){Check(!outgoing.Recent.Any(v=>v.Id=="bad"),"invalid captured target never enters send queue");}
}
var engineConfig=new ConfigStore(Path.Combine(folder,"engine"));engineConfig.Save(engineConfig.Current with{Mode="fast",IncomingTargetLanguage="ja"});var probe=new TargetProbe();await using(var engine=new JobEngine(engineConfig,probe)){
 engine.Exchange("s",[new("a","same")],[]);for(int i=0;i<100&&!engine.Exchange("s",[],["a"])[0].Done;i++)await Task.Delay(10);
 engineConfig.Save(engineConfig.Current with{IncomingTargetLanguage="ko"});engine.Exchange("s",[new("b","same")],[]);for(int i=0;i<100&&!engine.Exchange("s",[],["b"])[0].Done;i++)await Task.Delay(10);
 Check(engine.Exchange("s",[],["b"])[0].Text=="ko"&&probe.Calls==2,"fast-cache keys include receive target even without manual cache clear");
}
File.WriteAllText(Path.Combine(root,"artifacts/language-tests.json"),JsonSerializer.Serialize(new{passed,presetCount=TranslationLanguages.Presets.Length,scope="mock HTTP target routing, prompts, dictionary/cache isolation, migration and queue snapshots; no live cloud quality or game FPS measurement"},ConfigStore.Json));Console.WriteLine($"TOTAL {passed} passed");

sealed class TargetProbe:ITranslator {public int Calls;public Task<string> Fast(string text,Settings config,CancellationToken ct){Calls++;return Task.FromResult(config.IncomingTargetLanguage);}public Task<string> Quality(string text,string[] context,Settings config,CancellationToken ct)=>throw new NotSupportedException();}
sealed class Fixture:HttpMessageHandler {
 public int Calls;public List<string> Targets=[];public List<(string Id,string Name,string? Code)> Languages=[];
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){
  var body=req.Content==null?"":await req.Content.ReadAsStringAsync(ct);var url=req.RequestUri!.ToString();
  if(req.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=new StringContent("params_AbusePreventionHelper = [\"123\",\"token\"]; IG:\"ig\"")};
  Calls++;
  if(url.Contains("chat/completions")){
   using var payload=JsonDocument.Parse(body);using var input=JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
   var target=input.RootElement.GetProperty("targetLanguage");var id=target.GetProperty("id").GetString()!;var name=target.GetProperty("name").GetString()!;Languages.Add((id,name,null));
   return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{message=new{content=id+":"+input.RootElement.GetProperty("current").GetString()},finish_reason="stop"}}})};
  }
  string targetCode,text;
  if(url.Contains("ttranslatev3")){var form=body.Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1].Replace('+',' ')));targetCode=form["to"];text=form["text"];}
  else{targetCode=Regex.Match(url,@"(?:\?|&)to=([^&]+)").Groups[1].Value;using var input=JsonDocument.Parse(body);text=input.RootElement[0].GetProperty("text").GetString()!;}
  Targets.Add(targetCode);return new(HttpStatusCode.OK){Content=JsonContent.Create(new[]{new{translations=new[]{new{text=targetCode+":"+text}}}})};
 }
}
