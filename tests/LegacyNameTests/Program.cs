using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var temp=Path.Combine(root,"artifacts","legacy-names",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var defaults=Path.Combine(root,"src/Bridge/data/terms.json");var terms=new Terms(temp,defaults);var passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
string Resolve(string input,bool legacy){var p=terms.Protect(input,true,true,true);return terms.RenderChineseStyle(Terms.Restore(p.Text,p.Map),input,legacy);}
foreach(var (source,english,current,old) in new (string,string,string,string)[]{
    ("亚伯拉姆斯","Abrams","亚伯兰","亚伯拉姆斯"),("复仇女巫","Vindicta","薇妲","复仇女巫"),
    ("复仇之影","Vindicta","薇妲","复仇女巫"),("神鞭","Lash","劳什","神鞭"),
    ("隧底双煞","Mo & Krill","莫克双雄","隧底双煞"),("麦金尼斯","McGinnis","麦金妮","麦金尼斯"),
    ("守望者","Warden","沃督","守望者"),("暗影","Haze","岚梦","暗影"),
    ("奇能博士","Dynamo","奇能","奇能博士"),("凯尔文","Kelvin","开尔文","凯尔文"),
    ("西弗","Shiv","希弗","西弗"),("金属皮肤","Metal Skin","铜皮铁骨","金属皮肤"),
    ("水银装填","Quicksilver Reload","魔力装填","水银装填"),("高级子弹护甲","Bullet Resilience","子弹坚甲","高级子弹护甲"),
    ("高级元灵护甲","Spirit Resilience","元灵护体","高级元灵护甲"),("沉默符文","Silence Wave","沉默之潮","沉默符文"),
    ("诅咒遗物","Cursed Relic","天谴圣物","诅咒遗物"),("救援光束","Rescue Beam","营救光束","救援光束"),
    ("群体治疗环","Healing Nova","治疗之环","群体治疗环"),("反弹甲","Return Fire","回应射击","反弹甲"),
    ("守卫","Guardian","卫士","守卫"),("基地守卫","Base Guardians","基地卫士","基地守卫"),
    ("行者","Walker","机甲","行者"),("主宰","Patron","守护神","主宰"),
    ("灵魂坛","Urn","灵瓮","灵魂坛"),("复活强化","Rejuvenator","复生石","复活强化")
}){
    var term=terms.All.Single(t=>t.Source==source);
    Check(term.English==english&&term.Target==current&&term.LegacyTarget==old,"old spelling resolves canonical identity: "+source);
    var input="集火"+source;
    Check(Resolve(input,false)=="集火"+current+"（"+english+"）","current display: "+source);
    Check(Resolve(input,true)=="集火"+old+"（"+english+"）","legacy display: "+source);
    var p=terms.ProtectOutgoing(input);Check(Terms.Restore(p.Text,p.Map)=="集火"+english,"old spelling sends canonical English: "+source);
}
Check(Resolve("神鞭和劳什残血",true)=="神鞭（Lash）和神鞭（Lash）残血","new and old name for one hero retain separate occurrences");
Check(Resolve("隧底雙煞和復仇女巫",false)=="莫克双雄（Mo & Krill）和薇妲（Vindicta）","traditional legacy spellings normalize to current names");
Check(Resolve("拆行者，守主宰",false)=="拆机甲（Walker），守守护神（Patron）","objective old names resolve with tactical context");
Check(Resolve("Patron is vulnerable",false)=="守护神（Patron） is vulnerable","vulnerable objective context protects the current canonical name");
Check(Resolve("他是一位行者，暗影效果很漂亮",false)=="他是一位行者，暗影效果很漂亮","ordinary Chinese nouns are not forced into objectives or heroes");
Check(terms.RenderChineseStyle("一台机甲玩具","a mech toy",true)=="一台机甲玩具","unrelated translated words cannot be globally rewritten");
Check(terms.RenderChineseStyle("西尔芙（Silver）来了","wolf girl came",true)=="西尔芙（Silver）来了","unrecorded old name falls back to the current name");
foreach(var (old,canonical) in new[]{("Spirit Shredder Bullets","Spirit Shredder"),("Soul Shredder Bullets","Spirit Shredder"),("Improved Bullet Armor","Bullet Resilience"),("Improved Spirit Armor","Spirit Resilience"),("Silence Glyph","Silence Wave")}){
    var p=terms.ProtectOutgoing("buy "+old);Check(Terms.Restore(p.Text,p.Map)=="buy "+canonical,"old English item name retains current identity: "+old);
}

File.WriteAllText(Path.Combine(temp,"settings.json"),"{\"incomingScope\":\"all_players\",\"fontSize\":19}");
var store=new ConfigStore(temp);Check(store.Current.ChineseNameStyle=="current"&&store.Current.FontSize==19,"old configuration gets current names without changing existing choices");
store.Save(store.Current with{ChineseNameStyle="legacy",LlmKey="fake-local-key",AzureKey="fake-azure-key",LlmBaseUrl="https://fixture.invalid/v1",Model="fixture",FastProvider="azure",OutgoingMode="quality"});
Check(new ConfigStore(temp).Current.ChineseNameStyle=="legacy","legacy choice persists across restart");
try{ConfigStore.Validate(store.Current with{ChineseNameStyle="wrong"});throw new Exception("expected validation");}catch(ArgumentException){Check(true,"invalid display style is rejected");}
try{Terms.Validate([new("source","target","english",LegacyTarget:new string('a',151))]);throw new Exception("expected validation");}catch(ArgumentException){Check(true,"overlong old name is rejected");}
try{Terms.Validate([new("source","target","english",LegacyTarget:null!)]);throw new Exception("expected validation");}catch(ArgumentException){Check(true,"null old name is rejected");}
var imported=JsonSerializer.Deserialize<Term[]>("[{\"source\":\"custom\",\"target\":\"当前名\",\"english\":\"Custom\",\"kind\":\"name\",\"ambiguous\":false}]",ConfigStore.Json)!;
Terms.Validate(imported);Check(imported[0].LegacyTarget=="","old-format glossary imports without the new optional field");
var custom=new Terms(Path.Combine(temp,"custom"),Path.Combine(temp,"none.json"));Directory.CreateDirectory(Path.Combine(temp,"custom"));custom.Save([new("Custom","当前名","Custom",LegacyTarget:"旧名")]);
Check(custom.RenderChineseStyle("当前名（Custom）","Custom",true)=="旧名（Custom）","a custom old display name renders as plain text");
custom.Save([new("custom phrase","护送灵瓮","","phrase",LegacyTarget:"运灵魂坛")]);
Check(custom.RenderChineseStyle("请护送灵瓮","custom phrase",true)=="请运灵魂坛","a custom phrase can explicitly supply its own old wording");
var blankPreference=new Terms(Path.Combine(temp,"blank-preference"),defaults);Directory.CreateDirectory(Path.Combine(temp,"blank-preference"));
blankPreference.Save(blankPreference.All.Select(t=>t.Source=="神鞭"?t with{LegacyTarget=""}:t).ToArray());
Check(blankPreference.RenderChineseStyle("劳什（Lash）","神鞭",true)=="劳什（Lash）","blank old name on the matched entry overrides other aliases' defaults");
blankPreference.Save(blankPreference.All.Select(t=>t.Source=="基地守卫"?t with{LegacyTarget=""}:t).ToArray());
Check(blankPreference.RenderChineseStyle("基地卫士（Base Guardians）","基地守卫",true)=="基地卫士（Base Guardians）","blank longer name blocks an overlapping shorter objective replacement");

using var fixture=new Fixture();using var http=new HttpClient(fixture);using var translators=new Translators(store,terms,http);
var currentSettings=store.Current with{ChineseNameStyle="current"};var legacy=store.Current;
Check(await translators.Fast("神鞭",currentSettings,CancellationToken.None)=="劳什（Lash）","current exact result stays canonical");
Check(await translators.Fast("神鞭",legacy,CancellationToken.None)=="神鞭（Lash）","legacy exact result needs no cloud request");
Check(await translators.Fast("take urn",currentSettings,CancellationToken.None)=="拿灵瓮","current exact objective phrase");
Check(await translators.Fast("take urn",legacy,CancellationToken.None)=="拿灵魂坛","legacy style applies inside exact phrases");
var line="Lash has no ult, Vindicta is low";
Check((await translators.Fast(line,legacy,CancellationToken.None)).Contains("神鞭（Lash）")&&fixture.AzureCalls==1,"fast result chooses legacy names after cloud placeholders restore");
var quality=await translators.Quality(line,[],legacy,CancellationToken.None);Check(quality.Contains("神鞭（Lash）")&&quality.Contains("复仇女巫（Vindicta）"),"quality result chooses legacy display after canonical validation");
using(var payload=JsonDocument.Parse(fixture.LastInput!))Check(payload.RootElement.GetProperty("requiredNames").EnumerateArray().Select(v=>v.GetString()).SequenceEqual(new[]{"劳什","薇妲"}),"quality request still requires current canonical names internally");
var calls=fixture.LlmCalls;quality=await translators.Quality(line,[],currentSettings,CancellationToken.None);
Check(quality.Contains("劳什（Lash）")&&fixture.LlmCalls==calls+1,"opposite display style cannot reuse a legacy quality cache entry");
await translators.Quality(line,[],legacy,CancellationToken.None);Check(fixture.LlmCalls==calls+1,"same style still reuses its own quality cache");
quality=await translators.Quality("take urn now",[],legacy,CancellationToken.None);Check(quality=="现在拿灵魂坛","legacy rendering handles a reordered objective phrase");
foreach(var language in new[]{"en","ru","ja","zh-Hant"}){
    var c=legacy with{IncomingTargetLanguage=language};Check(await translators.Fast("神鞭",c,CancellationToken.None)=="Lash","legacy Chinese display does not force Chinese into target "+language);
}
Check(await translators.TranslateOutgoing("神鞭和复仇女巫残血",legacy,false,CancellationToken.None)=="Lash和Vindicta残血","legacy input and display preference never send old Chinese names instead of canonical English");

var queueConfig=new ConfigStore(Path.Combine(temp,"queue"));queueConfig.Save(queueConfig.Current with{Mode="fast"});var probe=new NameProbe();await using(var engine=new JobEngine(queueConfig,probe)){
    engine.Exchange("s",[new("one","same")],[]);await Done(engine,"one");
    queueConfig.Save(queueConfig.Current with{ChineseNameStyle="legacy"});engine.Exchange("s",[new("two","same")],[]);await Done(engine,"two");
    Check(engine.Exchange("s",[],["two"])[0].Text=="legacy"&&probe.Calls==2,"fast cache is isolated by display choice");
}
var times=new List<double>();for(var i=0;i<220;i++){var timer=System.Diagnostics.Stopwatch.StartNew();terms.RenderChineseStyle("劳什（Lash）和薇妲（Vindicta）没大招，拿灵瓮","Lash has no ult, Vindicta is low, take urn",true);timer.Stop();if(i>=20)times.Add(timer.Elapsed.TotalMilliseconds);}times.Sort();
File.WriteAllText(Path.Combine(root,"artifacts/legacy-name-tests.json"),JsonSerializer.Serialize(new{passed,totalTerms=terms.All.Length,legacyRenderP50Ms=times[times.Count/2],legacyRenderP95Ms=times[(int)(times.Count*.95)],scope="mock cloud and isolated configuration/glossary/cache; no real game or cloud quality verification"},ConfigStore.Json));
Console.WriteLine($"TOTAL {passed} passed; legacy rendering P95={times[(int)(times.Count*.95)]:F2} ms");
static async Task Done(JobEngine engine,string id){for(var i=0;i<200;i++){if(engine.Exchange("s",[],[id])[0].Done)return;await Task.Delay(10);}throw new TimeoutException();}
sealed class NameProbe:ITranslator{public int Calls;public Task<string> Fast(string t,Settings c,CancellationToken ct){Calls++;return Task.FromResult(c.ChineseNameStyle);}public Task<string> Quality(string t,string[] context,Settings c,CancellationToken ct)=>throw new NotSupportedException();}
sealed class Fixture:HttpMessageHandler {
    public int LlmCalls,AzureCalls;public string? LastInput;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        if(request.RequestUri!.AbsolutePath.EndsWith("chat/completions")){
            LlmCalls++;LastInput=body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();using var input=JsonDocument.Parse(LastInput!);var text=input.RootElement.GetProperty("current").GetString();
            if(text=="take urn now")text="现在拿灵瓮";
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason="stop",message=new{content=text}}}})};
        }
        AzureCalls++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new[]{new{translations=new[]{new{text=body.RootElement[0].GetProperty("text").GetString()}}}})};
    }
}
