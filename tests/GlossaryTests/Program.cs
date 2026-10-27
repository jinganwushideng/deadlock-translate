using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var temp=Path.Combine(root,"artifacts","glossary-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var terms=new Terms(temp,Path.Combine(root,"src/Bridge/data/terms.json"));
var passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
string IncomingNames(string text){var p=terms.Protect(text,false,true,true);return Terms.Restore(p.Text,p.Map);}
string OutgoingNames(string text){var p=terms.ProtectOutgoing(text);return Terms.Restore(p.Text,p.Map);}

foreach(var (input,expected) in new (string,string)[]{
    ("蓝牛没大招，狼女在后面","亚伯兰没大招，西尔芙在后面"),
    ("蓝牛和狼女来了","亚伯兰和西尔芙来了"),
    ("藍牛和希爾芙來了","亚伯兰和西尔芙來了"),
    ("希尔芙和西尔芙","西尔芙和西尔芙"),
    ("炮台姐跟机枪妹走","麦金妮跟麦金妮走"),
    ("卡牌姐和黑帮女","灵魅和灵魅"),
    ("飞天狙和灯泡女","薇妲和悖论"),
    ("凝胶人跟绿泡泡","魔液跟魔液"),
    ("赛文没大招","柒没大招"),
    ("blue cow no ult, WOLFGIRL behind us","亚伯兰 no ult, 西尔芙 behind us"),
    ("blue bull and wolf-girl","亚伯兰 and 西尔芙"),
    ("cow no ult, wolf is low","亚伯兰 no ult, 西尔芙 is low"),
    ("bull has no ult, werewolf missing","亚伯兰 has no ult, 西尔芙 missing"),
    ("Silver has no ult, silver medal","西尔芙 has no ult, silver medal"),
    ("蓝牛no ult，Bebop没大招","亚伯兰no ult，比波普没大招"),
    ("абрамсу и сильвера","亚伯兰 и 西尔芙"),
    ("СИЛЬВЕРУ и сильвером","西尔芙 и 西尔芙"),
    ("волчица без ульты","西尔芙 без ульты"),
    ("бей волчицу, волчице нет ульты","бей 西尔芙, 西尔芙 нет ульты"),
    ("оборотень на кд, оборотня сзади","西尔芙 на кд, 西尔芙 сзади"),
    ("集火口袋，我的口袋空了","集火口袋，我的口袋空了"), // Target-only names are handled by outgoing normalization.
    ("集火机器人，机器人展览很好看","集火比波普，机器人展览很好看"),
    ("警长没大招，去找警长报案","沃督没大招，去找警长报案"),
    ("集火刀姐","集火大和"),
    ("wolf no ult, a wolf howls","西尔芙 no ult, a wolf howls"),
    ("волчица без ульты, волчица в лесу","西尔芙 без ульты, волчица в лесу"),
    ("blue cow no ult and cow no ult","亚伯兰 no ult and 亚伯兰 no ult"),
    ("狼女，狼女，蓝牛","西尔芙，西尔芙，亚伯兰")
})Check(IncomingNames(input)==expected,"incoming fixture: "+input);

foreach(var text in new[]{"the cow eats grass","a wolf howls","silver medal","a werewolf story","волчица в лесу","оборотень в сказке","wolfpack bullseye coward silvery","bluecowhelper wolfgirlfriend","Bebopper qsrhelper","75 seconds, 100ms latency","我的口袋空了","警长在警局","金身雕像很好看"}){
    Check(IncomingNames(text)==text,"ordinary words stay intact: "+text);
    Check(OutgoingNames(text)==text,"outgoing ordinary words stay intact: "+text);
}
foreach(var (input,expected) in new (string,string)[]{
    ("蓝牛和狼女来了","Abrams和Silver来了"),
    ("藍牛和希爾芙","Abrams和Silver"),
    ("希尔芙来支援","Silver来支援"),
    ("wolf girl来支援","Silver来支援"),
    ("абрамсу и сильвера","Abrams и Silver"),
    ("волчица без ульты","Silver без ульты"),
    ("集火口袋，我的口袋空了","集火Pocket，我的口袋空了"),
    ("口袋没大招，机器人残血","Pocket没大招，Bebop残血"),
    ("机枪妹救一下卡牌姐","McGinnis救一下Wraith"),
    ("牛头人和狼群","牛头人和狼群")
})Check(OutgoingNames(input)==expected,"outgoing fixture: "+input);
var repeat=terms.Protect("blue cow and blue cow and 蓝牛",true,true,true);
Check(repeat.Map.Count==3&&repeat.Map.Values.All(v=>v=="亚伯兰（Abrams）"),"longest match and repeated aliases have one placeholder per occurrence");
Check(terms.Exact("蓝牛",true)=="亚伯兰（Abrams）"&&terms.Exact("狼女",true)=="西尔芙（Silver）","explicit nicknames support bilingual exact lookup");
Check(terms.Exact("wolf",true)==null&&terms.Exact("silver",true)==null&&terms.Exact("волчица",true)==null,"ordinary ambiguous words cannot bypass context through exact lookup");

var store=new ConfigStore(temp);store.Save(store.Current with{LlmBaseUrl="https://fixture.invalid/v1",LlmKey="fake-local-key",AzureKey="fake-azure-key",Model="fixture",FastProvider="azure",OutgoingMode="quality"});
using var fixture=new Fixture();using var http=new HttpClient(fixture);using var translators=new Translators(store,terms,http);
var settings=store.Current with{BilingualNames=true};
var fast=await translators.Fast("blue cow no ult, wolf girl is behind",settings,CancellationToken.None);
Check(fast=="亚伯兰（Abrams） 没大招, 西尔芙（Silver） is behind","incoming fast protects foreign nicknames and restores Chinese bilingual names: "+fast);
fast=await translators.Fast("wolf no ult, a wolf howls",settings,CancellationToken.None);
Check(fast=="西尔芙（Silver） 没大招, a wolf howls","fast resolves only the tactical occurrence of an ambiguous foreign word: "+fast);
fast=await translators.Fast("狼女来支援",settings with{IncomingTargetLanguage="ru"},CancellationToken.None);
Check(fast=="Silver来支援"&&fixture.LastTarget=="ru","non-Chinese reception retains nickname certainty and canonical English");
var sent=await translators.TranslateOutgoing("蓝牛和狼女来了",settings,true,CancellationToken.None);
Check(sent=="Abrams和Silver来了"&&fixture.LastTarget=="en","fast sending protects resolved Chinese aliases before a cloud service sees them");
sent=await translators.TranslateOutgoing("blue cow no ult, волчица без ульты",settings with{OutgoingTargetLanguage="ja"},true,CancellationToken.None);
Check(sent=="Abrams no ult, Silver без ульты"&&fixture.LastTarget=="ja","fast sending handles English/Russian aliases for another target language");
var quality=await translators.Quality("blue cow no ult, волчица без ульты",[],settings,CancellationToken.None);
Check(quality=="亚伯兰（Abrams） no ult, 西尔芙（Silver） без ульты","incoming quality receives canonical names and returns bilingual names");
quality=await translators.Quality("wolf girl came",[],settings with{IncomingTargetLanguage="ko"},CancellationToken.None);
Check(quality=="Silver came","international quality receives canonical English for an explicit foreign alias");
sent=await translators.TranslateOutgoing("蓝牛和狼女来支援",settings,false,CancellationToken.None);
Check(sent=="Abrams和Silver来支援","quality sending uses resolved Chinese aliases without losing ambiguity decisions");
using(var input=JsonDocument.Parse(fixture.LastInput!)){
    Check(input.RootElement.GetProperty("requiredNames").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(new[]{"Abrams","Silver"}),"outgoing prompt requires the resolved canonical names");
    Check(input.RootElement.GetProperty("glossary").GetArrayLength()==0,"outgoing prompt omits redundant resolved alias mappings");
}
sent=await translators.TranslateOutgoing("бей волчицу",settings,false,CancellationToken.None);
Check(sent=="бей Silver"&&fixture.LastInput!.Contains("Silver")&&!fixture.LastInput.Contains("волчицу"),"foreign aliases are resolved once before the compact quality request");
fixture.OverrideReply="abrams and silver, help!";
sent=await translators.TranslateOutgoing("蓝牛和狼女帮一下",settings,false,CancellationToken.None);
Check(sent=="Abrams and Silver, help!","quality normalizes harmless English name casing");
fixture.OverrideReply="Abramsian and Silverware";
try{await translators.TranslateOutgoing("蓝牛和狼女支援一下",settings,false,CancellationToken.None);throw new Exception("expected whole-name guard");}
catch(TranslationOutputException){Check(true,"longer unrelated words cannot satisfy required canonical names");}
fixture.OverrideReply="lost game names";
try{await translators.TranslateOutgoing("蓝牛和狼女快来",settings,false,CancellationToken.None);throw new Exception("expected name guard");}
catch(TranslationOutputException){Check(true,"quality sending rejects missing required hero names instead of sending an incorrect message");}

// This measures backend dictionary work only; it is not game FPS or cloud latency.
var timings=new List<double>();
var benchmarkText="蓝牛没大招，狼女在后面，wolf no ult, волчица без ульты，买水银装填";
for(var i=0;i<20;i++){terms.Protect(benchmarkText,true,true);terms.ProtectOutgoing(benchmarkText);}
for(var i=0;i<200;i++){var timer=System.Diagnostics.Stopwatch.StartNew();terms.Protect(benchmarkText,true,true);terms.ProtectOutgoing(benchmarkText);timer.Stop();timings.Add(timer.Elapsed.TotalMilliseconds);}
timings.Sort();
File.WriteAllText(Path.Combine(root,"artifacts/glossary-tests.json"),JsonSerializer.Serialize(new{passed,totalTerms=terms.All.Length,backendPairP50Ms=timings[timings.Count/2],backendPairP95Ms=timings[(int)(timings.Count*.95)],backendPairMaxMs=timings[^1],scope="deterministic aliases and mock HTTP; excludes real game FPS and real cloud translation quality",at=DateTime.UtcNow},ConfigStore.Json));
Console.WriteLine($"TOTAL {passed} passed; dictionary pair P95={timings[(int)(timings.Count*.95)]:F2} ms");

sealed class Fixture:HttpMessageHandler {
    public string? LastInput;public string? OverrideReply;public string? LastTarget;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        var body=await request.Content!.ReadAsStringAsync(ct);using var json=JsonDocument.Parse(body);
        if(request.RequestUri!.AbsolutePath.EndsWith("chat/completions")){
            LastInput=json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var input=JsonDocument.Parse(LastInput);var reply=OverrideReply??input.RootElement.GetProperty("current").GetString();
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason="stop",message=new{content=reply}}}})};
        }
        LastTarget=request.RequestUri.Query.Split("to=")[^1];
        return new(HttpStatusCode.OK){Content=JsonContent.Create(new[]{new{translations=new[]{new{text=json.RootElement[0].GetProperty("text").GetString()}}}})};
    }
}
