using System.Net;
using System.Text;
using System.Text.Json;
using DeadlockTranslate;

internal static class BuildPromptFixture {
    internal static async Task Run(string folder,Action<bool,string> check){
        var path=Path.Combine(folder,"prompt-fixture");Directory.CreateDirectory(path);
        var config=new ConfigStore(path);
        config.Save(new Settings{LlmKey="local-fixture",LlmBaseUrl="http://127.0.0.1:9/v1",LlmStreaming=false,BilingualNames=true});
        var terms=new Terms(path,Path.Combine(path,"no-defaults.json"));
        terms.Save([new("Haze","岚梦","Haze"),new("Tankbuster","重装克星","Tankbuster")]);
        using var handler=new Capture();using var http=new HttpClient(handler);
        using var translators=new Translators(config,terms,http);
        var source=new[]{"Haze - Gun-Initiator","Pick Rate: 24%\nWR: +3%","M1 buff"};
        var fields=new[]{new BuildSourceField("title","title",source[0]),new("item:7","item-note",source[1],99),new("item:8","item-note",source[2],100)};
        var result=await translators.TranslateBuildQuality(source,"Haze build: buy Tankbuster then poke",BuildTextProfile.Resolve(config.Current),default,
            buildTitle:source[0],fields:fields);
        check(handler.Input!.RootElement.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("kind").GetString()).SequenceEqual(new[]{"title","item-note","item-note"}),"actual quality request carries native field kinds for title and equipment annotations");
        var names=handler.Input.RootElement.GetProperty("referenceNames").EnumerateArray().Select(v=>v.GetString()).ToArray();
        check(names.Contains("岚梦（Haze）")&&names.Contains("重装克星（Tankbuster）"),"context supplies canonical dictionary names without making all references mandatory in each result");
        check(result[0].Contains("岚梦（Haze）")&&result[1]=="装备购买率：24%\n胜率：+3%"&&result[2]=="普攻增益","parsed build results retain protected names, signed stats and concise annotation text");
        terms.Save([new("Tankbuster","自定重装名","Tankbuster"),new("Toxic Bullets","自定毒弹名","Toxic Bullets"),
            new("Mystic Reverb","秘术余波","Mystic Reverb"),new("Mystic Regeneration","秘术再生","Mystic Regeneration"),
            new("Radiant Regeneration","光耀再生","Radiant Regeneration")]);
        var aliases=BuildTextAbbreviations.Match("tb then Mystic.R/Radiant.R",terms,false,true);
        check(aliases.Single(a=>a.Source=="tb").Candidates.ToHashSet().SetEquals(new[]{"自定重装名","自定毒弹名"}),"ambiguous document abbreviations keep multiple candidates and respect the active custom dictionary");
        check(aliases.Single(a=>a.Source=="mystic.r").Candidates.Length==2&&aliases.Single(a=>a.Source=="radiant.r").Candidates.Length==1,"dotted equipment abbreviations retain all matching canonical names rather than guessing");
        check(BuildTextAbbreviations.Match("me is this theme",terms,true,true).Length==0&&
            BuildTextAbbreviations.Match("twitch.tv/tb https://example.com/tb",terms,true,true).Length==0,
            "ordinary English words and URL handles never acquire item abbreviation hints");
        handler.Input.Dispose();
    }
    private sealed class Capture:HttpMessageHandler {
        internal JsonDocument? Input;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation){
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            var messages=json.RootElement.GetProperty("messages");
            Input=JsonDocument.Parse(messages[messages.GetArrayLength()-1].GetProperty("content").GetString()!);
            var translated=JsonSerializer.Serialize(new[]{new{id=0,translation="岚梦（Haze）-枪械先手流"},new{id=1,translation="装备购买率：24%\n胜率：+3%"},new{id=2,translation="普攻增益"}});
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=translated},finish_reason="stop"}}}),Encoding.UTF8,"application/json")};
        }
    }
}
