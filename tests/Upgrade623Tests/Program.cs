using DeadlockTranslate;
using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Net;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var temp=Path.Combine(root,"artifacts","upgrade-0623-tests",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var passed=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
void Reject(Action action,string name){try{action();}catch(Exception ex)when(ex is ArgumentException or InvalidDataException){Check(true,name);return;}throw new Exception("FAIL accepted "+name);}
var defaults=new Settings();
Check(!defaults.OutgoingSeparateBilingual&&!defaults.OutgoingSplitLong&&defaults.QuickMessages.Length==0,"new sending modes are opt-in");
Check(LlmProfiles.Effective(defaults,true).LlmThinkingEnabled&&LlmProfiles.Effective(defaults,false).Model==defaults.Model,"existing thinking and model inherited");
var custom=defaults with{LlmIndependentProfiles=true,IncomingModel="receive-model-full",OutgoingModel="send-model-full",IncomingThinking=false,OutgoingThinking=true};
Check(LlmProfiles.Effective(custom,false).Model=="receive-model-full"&&!LlmProfiles.Effective(custom,false).LlmThinkingEnabled,"incoming profile resolved independently");
Check(LlmProfiles.Effective(custom,true).Model=="send-model-full"&&LlmProfiles.Effective(custom,true).LlmThinkingEnabled,"outgoing profile resolved independently");
Check(!defaults.LlmIndependentProfiles,"fresh setup uses the common profile by default");
var disabled=custom with{LlmIndependentProfiles=false};
Check(LlmProfiles.Effective(disabled,false).Model==defaults.Model&&LlmProfiles.Effective(disabled,false).LlmThinkingEnabled==defaults.LlmThinkingEnabled,"disabled profiles truly use common model and thinking");
Check(disabled.IncomingModel==custom.IncomingModel&&disabled.IncomingThinking==custom.IncomingThinking,"disabling profiles retains independent values");
var migration=Path.Combine(temp,"migration");Directory.CreateDirectory(migration);
var oldValues=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(custom,ConfigStore.Json))!;oldValues.Remove("llmIndependentProfiles");
File.WriteAllText(Path.Combine(migration,"settings.json"),JsonSerializer.Serialize(oldValues));var migrated=new ConfigStore(migration);
Check(migrated.Current.LlmIndependentProfiles&&migrated.Current.IncomingModel==custom.IncomingModel,"old independent configuration migrates enabled without losing model");
migrated.Save(migrated.Current with{LlmIndependentProfiles=false});
var reloaded=new ConfigStore(migration);Check(!reloaded.Current.LlmIndependentProfiles&&reloaded.Current.IncomingModel==custom.IncomingModel,"explicit disabled choice survives restart even with saved independent values");

Check(OutgoingParts.Build("你好","hello","bilingual",false,false).SequenceEqual(["你好 | hello"]),"legacy bilingual single line unchanged");
Check(OutgoingParts.Build("你好","hello","bilingual",true,false).SequenceEqual(["你好","hello"]),"bilingual parts ordered original first");
Check(OutgoingParts.Build("Hello","hello","bilingual",true,false).Length==1,"identical bilingual output sent once");
Check(OutgoingParts.Build("",new string('x',501),"translation",false,false).SequenceEqual([new string('x',80)]),"splitting off sends the maximum single-message prefix");
var longText=new string('中',240);
Check(OutgoingParts.Build("",longText,"translation",false,true).SequenceEqual([new string('中',80),new string('中',80),new string('中',80)]),"long message fully preserved across native 80-character pieces");
var emoji=string.Concat(Enumerable.Repeat("👩🏽‍🚀",100));var parts=OutgoingParts.Build("",emoji,"translation",false,true);
Check(string.Concat(parts)==emoji&&parts.All(p=>OutgoingParts.CharacterCount(p)<=80)&&parts.All(p=>StringInfo.ParseCombiningCharacters(p).All(n=>p[n]!='\u200d')),"emoji and surrogate sequences stay intact");
var combining=string.Concat(Enumerable.Repeat("!\u0301",310));parts=OutgoingParts.Build("",combining,"translation",false,true);
Check(string.Concat(parts)==combining&&parts.All(p=>!char.IsSurrogate(p[^1])&&p[0]!='\u0301'),"punctuation split cannot detach combining marks");
Reject(()=>OutgoingParts.Build("",new string('x',4100),"translation",false,true),"more than eight parts rejected before sending");
Reject(()=>OutgoingParts.Build("","a"+new string('\u0301',501),"translation",false,true),"oversized grapheme rejected without truncation");
Check(OutgoingParts.Build("","a;\"b\\c\n d","translation",false,true)[0]=="a,'b/c d","every part uses existing plain-text safety rules");
Check(OutgoingParts.Build("",new string('x',80),"translation",false,false).Single().Length==80,"native ASCII boundary is 80, not 500");
Check(OutgoingParts.Plan("",new string('x',81),"translation",false,false) is {Parts:[{Length:80}],OmittedCharacters:1},"81 ASCII characters clip to 80 when splitting disabled");
Check(OutgoingParts.Build("",new string('中',80),"translation",false,false).Single()==new string('中',80),"native limit counts characters rather than UTF-8 bytes");
var astral=string.Concat(Enumerable.Repeat("😀",80));
Check(OutgoingParts.Build("",astral,"translation",false,false).Single()==astral&&OutgoingParts.CharacterCount(astral)==80,"native UTF-32 count admits 80 supplementary characters");
Check(OutgoingParts.Build("",astral+"😀","translation",false,false).Single()==astral,"supplementary prefix fills 80 native characters without splitting a surrogate");
Reject(()=>OutgoingParts.Build("","broken\ud800","translation",false,true),"unpaired UTF-16 surrogate refused before sending");
var observedOriginal=string.Concat(Enumerable.Repeat("测试",40));var observedTranslation=string.Join(" ",Enumerable.Repeat("test",60));
Check(OutgoingParts.Plan(observedOriginal,observedTranslation,"bilingual",false,false) is {Parts:[{} clipped],OmittedCharacters:302}&&clipped==observedOriginal,"combined bilingual clipping keeps original-first order and reports the omitted tail");
var separated=OutgoingParts.Plan(observedOriginal,observedTranslation,"bilingual",true,false);
Check(separated.Parts.Length==2&&separated.Parts[0]==observedOriginal&&separated.Parts[1]==observedTranslation[..80].TrimEnd()&&separated.OmittedCharacters==220,"separate bilingual mode clips each side independently without extra continuation");
var clusterPrefix=new string('a',79)+"👩🏽‍🚀";
Check(OutgoingParts.Plan("",clusterPrefix,"translation",false,false) is {Parts:[{Length:79}],OmittedCharacters:4},"clipping leaves a whole composite emoji out when remaining space cannot fit it");
Check(OutgoingParts.Plan("",new string('a',79)+"e\u0301","translation",false,false) is {Parts:[{Length:79}],OmittedCharacters:2},"clipping never detaches a combining mark from its base");
Reject(()=>OutgoingParts.Plan("","a"+new string('\u0301',80),"translation",false,false),"no prefix is sent when the first intact character group cannot fit");
var observedParts=OutgoingParts.Build(observedOriginal,observedTranslation,"bilingual",false,true);
Check(observedParts.Length==5&&observedParts.All(p=>OutgoingParts.CharacterCount(p)<=80)&&OutgoingService.NormalizeLine(string.Join(" ",observedParts))==observedOriginal+" | "+observedTranslation,"observed bilingual payload split fully into five bounded messages");
Check(OutgoingParts.Build(string.Concat(Enumerable.Repeat("测试",12)),string.Join(" ",Enumerable.Repeat("test",12)),"bilingual",false,true).Length==2,"observed 86-character bilingual case splits into two");
var lengthView=new OutgoingView("length",observedOriginal,observedTranslation,"party","ready",null,SendText:observedParts[0],PartCount:observedParts.Length);
var length=SendLengthEvidence.For(lengthView)!;
Check(lengthView.SendCharacters==80&&lengthView.SendCharacterLimit==80&&length.OriginalCharacters==80&&length.TranslationCharacters==299&&length.CurrentPartUtf8Bytes==240,"length diagnostics count native characters and encoded bytes without text");
Check(DiagnosticLog.Clean(new(SendLength:length)).SendLength==length&&DiagnosticLog.Clean(new(SendLength:length with{PartCount=99})).SendLength==null,"length metadata is sanitized before logging");

var preset=new QuickMessage("q-1","group up","Ctrl+F1","team");
var settings=custom with{QuickMessages=[preset],LlmBaseUrl="http://localhost:19090/v1",LlmThinkingApi="deepseek",LlmStreaming=false,LlmKey="fixture-secret",OutgoingMode="quality"};
ConfigStore.Validate(settings);
Reject(()=>ConfigStore.Validate(settings with{QuickMessages=Enumerable.Range(0,11).Select(i=>preset with{Id="q-"+i}).ToArray()}),"eleven quick messages rejected");
Reject(()=>ConfigStore.Validate(settings with{QuickMessages=[preset,preset with{Id="q-2",Hotkey="LCtrl+F1"}]}),"generic and left modifier overlap rejected");
Reject(()=>ConfigStore.Validate(settings with{QuickMessages=[preset with{Hotkey="Alt+Space"}]}),"translation shortcut collision rejected");
Check(!QuickMessages.Overlap(HotkeyChord.Parse("LCtrl+F1"),HotkeyChord.Parse("Alt+F1")),"different modifiers do not conflict");
var hotkeys=new QuickMessageHotkeys();hotkeys.Configure(settings);hotkeys.Session("A",true);hotkeys.Seed(KeyModifiers.LeftControl);
Check(hotkeys.Process(112,0,0,true,false,1000,true),"preset key captured outside chat");
hotkeys.Process(112,0,0,true,false,2000,true);
Check(hotkeys.Take("A",2100).Length==1,"long press never repeats even after cooldown");
Check(hotkeys.Process(112,0,0,false,true,2200,false),"captured release consumed after focus loss");
hotkeys.Process(112,0,0x10,true,false,2300,true);
Check(hotkeys.Take("A",2400).Length==0,"injected shortcut ignored");
hotkeys.Process(112,0,0,true,false,2500,false);
Check(hotkeys.Take("A",2600).Length==0,"settings chat or other app disallows sending");
hotkeys.Process(112,0,0,false,true,2700,false);
hotkeys.Process(112,0,0,true,false,3000,true);hotkeys.Process(112,0,0,false,true,3100,true);
Check(hotkeys.Take("B",3200).Length==0,"old session press discarded");
hotkeys.Process(112,0,0,true,false,4000,true);hotkeys.Process(112,0,0,false,true,4100,true);
Check(hotkeys.Take("A",8000).Length==0,"stale shortcut is not replayed after bridge stall");
hotkeys.Session("A",false);hotkeys.Process(112,0,0,true,false,9000,true);
Check(hotkeys.Take("A",9100).Length==0,"mismatched game build disables new shortcut protocol");
hotkeys.Session("A",true);hotkeys.Process(112,0,0,true,false,9200,true);
Check(hotkeys.Take("A",9300).Length==0,"a held key does not trigger when focus or compatibility becomes ready");
hotkeys.RecoverRelease(_=>false);hotkeys.Process(112,0,0,true,false,10000,true);
Check(hotkeys.Take("A",10100).Length==1,"lost key-up can recover through bounded physical-key check");
hotkeys.Configure(settings);Check(hotkeys.Process(112,0,0,false,true,10200,true),"settings edit cannot lose a swallowed key release");

hotkeys.Configure(settings with{QuickMessages=[preset with{Channel="current"}]});hotkeys.Session("A",true,"party");hotkeys.Seed(KeyModifiers.LeftControl);
hotkeys.Process(112,0,0,true,false,12000,true);hotkeys.Session("A",true,"all");
Check(hotkeys.Take("A",12100).Single().Channel=="party","current channel shortcut retains channel sampled at keypress");
hotkeys.Process(112,0,0,false,true,12200,true);hotkeys.Session("A",true,"");hotkeys.Process(112,0,0,true,false,14000,true);
Check(hotkeys.Take("A",14100).Length==0,"unreadable current channel cannot authorize a preset");
var store=new ConfigStore(Path.Combine(temp,"config"));store.Save(settings);
Check(new ConfigStore(store.DirectoryPath).Current.QuickMessages[0]==preset,"quick phrase round trip");
var terms=new Terms(store.DirectoryPath,Path.Combine(root,"src/Bridge/data/terms.json"));
var preview=JsonSerializer.SerializeToElement(terms.Preview(new("蓝牛出个跑酷","outgoing"),settings),ConfigStore.Json);
Check(preview.GetProperty("localOnly").GetBoolean()&&preview.GetProperty("matches").GetArrayLength()>0,"glossary preview reports real protected aliases");
preview=JsonSerializer.SerializeToElement(terms.Preview(new("my pocket is empty"),settings),ConfigStore.Json);
Check(preview.GetProperty("matches").EnumerateArray().Any(m=>m.GetProperty("action").GetString()=="ambiguous"),"preview explains unresolved ordinary-word ambiguity");
Reject(()=>terms.Preview(new(new string('x',1025)),settings),"preview input bounded");

var handler=new Fixture();using var http=new HttpClient(handler);using var translators=new Translators(store,terms,http);
await translators.Quality("profile test incoming",[],settings,CancellationToken.None);
await translators.TranslateOutgoing("这是测试短句",settings,false,CancellationToken.None);
Check(handler.Requests[0].GetProperty("model").GetString()==settings.IncomingModel&&handler.Requests[0].GetProperty("thinking").GetProperty("type").GetString()=="disabled","incoming HTTP uses its model and thinking");
Check(handler.Requests[1].GetProperty("model").GetString()==settings.OutgoingModel&&handler.Requests[1].GetProperty("thinking").GetProperty("type").GetString()=="enabled","outgoing HTTP uses its model and thinking");
await translators.Quality("profile test incoming",[],settings with{OutgoingModel="other"},CancellationToken.None);
Check(handler.Requests.Count==2,"other direction profile does not pollute incoming cache");
await translators.Quality("profile test incoming",[],settings with{IncomingModel="new-incoming"},CancellationToken.None);
Check(handler.Requests.Count==3,"changed incoming model has separate cache identity");
await translators.Quality("profile test incoming",[],settings with{LlmIndependentProfiles=false},CancellationToken.None);
Check(handler.Requests.Count==4&&handler.Requests[^1].GetProperty("model").GetString()==settings.Model&&handler.Requests[^1].GetProperty("thinking").GetProperty("type").GetString()=="enabled","common switch changes actual request and cache identity");


store.Save(settings with{OutgoingSeparateBilingual=true,OutgoingSplitLong=true});
long now=1000;var calls=0;
await using var service=new OutgoingService(store,(text,config,force,token)=>{Interlocked.Increment(ref calls);return Task.FromResult("translated "+text);},()=>now);
service.Exchange("S",[new("one","原文","team","bilingual"),new("two","后续","team")],[]);
async Task WaitReady(string id){for(var i=0;i<100;i++){if(service.Recent.Any(v=>v.Id==id&&v.State is "ready" or "waiting_order"))return;await Task.Delay(10);}throw new Exception("Timed out "+id);}
await WaitReady("one");await WaitReady("two");
Check(service.Recent.Single(v=>v.Id=="one").PartCount==2&&service.Recent.Single(v=>v.Id=="two").State=="waiting_order","all parts retain original position in same-channel order");
service.Exchange("S",[],["one"]);
Check(service.Recent.Single(v=>v.Id=="one").State=="ready","legacy whole-task ack cannot skip multipart content");
service.Exchange("S",[],[],partAcks:[new("one",0),new("one",0),new("one",1)]);
Check(service.Recent.Single(v=>v.Id=="one").PartIndex==1&&service.Recent.Single(v=>v.Id=="one").State=="ready","duplicate or chained acknowledgements advance at most one part");
service.Exchange("S",[],[],partAcks:[new("one",0)]);
Check(service.Recent.Single(v=>v.Id=="one").PartIndex==1,"stale ack cannot advance later part");
service.Exchange("S",[],[],partAcks:[new("one",1)]);
Check(service.Recent.Single(v=>v.Id=="one").State=="submitted"&&service.Recent.Single(v=>v.Id=="two").State=="ready","next task unlocked only after all parts submitted");
var before=calls;service.Exchange("S",[],[],presets:[preset]);
Check(calls==before&&service.Recent.Any(v=>v.IsPreset&&v.SendText==preset.Text),"saved preset uses existing queue with no translation call");
var count=service.Recent.Length;service.Exchange("S",[],[],presets:[preset with{Text="forged"}]);
Check(service.Recent.Length==count,"unsaved or changed preset cannot be admitted");
service.Exchange("S",[new("partial","待续","all","bilingual")],[]);await WaitReady("partial");
service.Exchange("S",[],[],partAcks:[new("partial",0)]);
service.Exchange("S",[],[],refused:[new("partial","submit-rejected")]);
Check(service.Recent.Single(v=>v.Id=="partial") is {State:"failed",PartIndex:1},"partial submission failure retains progress and stops remaining text");
service.Exchange("S",[new("old","旧消息","party","bilingual")],[]);await WaitReady("old");
service.Exchange("T",[],[],partAcks:[new("old",0)]);
Check(service.Recent.Length==0,"cross-session transition discards all pending fragments");
service.Exchange("T",[new("timeout","超时","team","bilingual")],[]);await WaitReady("timeout");now+=40000;
Check(service.Recent.Single(v=>v.Id=="timeout").State=="failed","expired parts never stay ready indefinitely");
service.Exchange("T",[],[],partAcks:[new("timeout",0)]);
Check(service.Recent.Single(v=>v.Id=="timeout").State=="failed","late part acknowledgement cannot revive an expired task");
store.Save(settings with{OutgoingSeparateBilingual=false,OutgoingSplitLong=false});
await using(var lengthLog=new DiagnosticLog(Path.Combine(temp,"length-log"))) {
 await using var lengthService=new OutgoingService(store,(text,config,force,token)=>Task.FromResult(observedTranslation),log:lengthLog);
 async Task<OutgoingView> LengthState(string id,string state) {
  for(var i=0;i<100;i++){var view=lengthService.Recent.Single(v=>v.Id==id);if(view.State==state)return view;await Task.Delay(10);}
  throw new Exception("Timed out length fixture "+id);
 }
 lengthService.Exchange("L",[new("off",observedOriginal,"party","bilingual")],[]);
 var clippedView=await LengthState("off","ready");
 Check(clippedView.English==observedTranslation&&clippedView.Original==observedOriginal&&clippedView.SendText==observedOriginal&&clippedView.Truncated&&clippedView.OmittedCharacters==302&&clippedView.Error==null,"real service sends a bounded prefix and retains full output when splitting off");
 lengthService.Exchange("L",[],["off"]);
 Check(lengthService.Recent.Single(v=>v.Id=="off") is {State:"submitted",Truncated:true,OmittedCharacters:302},"submitted single-part task preserves its omitted-tail warning");
 store.Save(store.Current with{OutgoingSplitLong=true});
 lengthService.Exchange("L",[new("on",observedOriginal,"party","bilingual")],[]);
 var readyLength=await LengthState("on","ready");
 Check(readyLength.PartCount==5&&readyLength.SendCharacters==80&&readyLength.SendCharacterLimit==80,"real sending service exposes five native-sized parts and the actual limit");
 for(var i=0;i<5;i++) {
  var view=lengthService.Recent.Single(v=>v.Id=="on");
  Check(view.PartIndex==i&&view.SendCharacters<=80,"all emitted parts respect native character limit "+i);
  lengthService.Exchange("L",[],[],partAcks:[new("on",i)]);
 }
 Check(lengthService.Recent.Single(v=>v.Id=="on").State=="submitted","all five parts are acknowledged in order");
 lengthService.Exchange("L",[new("truncated","saved draft","party")],[]);await LengthState("truncated","ready");
 lengthService.Exchange("L",[],[],refused:[new("truncated","input-truncated")]);
 Check(lengthService.Recent.Single(v=>v.Id=="truncated") is {State:"failed",Error:{} error}&&error.Contains("游戏输入框"),"native readback refusal appears with a concrete remedy");
 var longPreset=preset with{Text=new string('q',100)};store.Save(store.Current with{OutgoingSplitLong=false,QuickMessages=[longPreset]});
 lengthService.Exchange("L",[],[],presets:[longPreset]);
 Check(lengthService.Recent.Single(v=>v.IsPreset) is {State:"ready",Truncated:true,OmittedCharacters:20,SendCharacters:80},"saved quick phrases follow the same single-part capacity rule");
 await lengthLog.FlushAsync();var recorded=string.Join("",(await lengthLog.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(recorded.Contains("sendLength")&&recorded.Contains("NativeInputTruncated")&&recorded.Contains("\"omittedCharacters\":302")&&!recorded.Contains(observedOriginal)&&!recorded.Contains(observedTranslation),"length diagnosis persists omitted count and refusal without storing chat text");
}
var clean=new PerformanceSample(double.NaN,-1,5,2,13,100,3,5).Clean();
Check(clean.DiscoveryMaxMs==0&&clean.ReadMaxMs==0&&clean.RenderMaxMs==5,"performance log values bounded");
Check(DiagnosticLog.Clean(new(BuildId:BuildInfo.Id)).BuildId==BuildInfo.Id&&DiagnosticLog.Clean(new(BuildId:"bad/key")).BuildId==null,"build identity logged with fixed character schema");
File.WriteAllText(Path.Combine(temp,"result.json"),JsonSerializer.Serialize(new{passed,cloudUsed=false,gameTested=false}));
Console.WriteLine("TOTAL "+passed+" passed");

sealed class Fixture:HttpMessageHandler {
 public List<JsonElement> Requests=new();
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){Requests.Add(JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone());return new(HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"fixture translation\"},\"finish_reason\":\"stop\"}]}",Encoding.UTF8,"application/json")};}
}
