using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeadlockTranslate;

internal sealed record BuildTextProfile(string Mode, Settings Fast, Settings Quality) {
    internal static BuildTextProfile Resolve(Settings settings) {
        var fast=settings with {
            IncomingTargetLanguage=settings.BuildTextTargetLanguage,
            IncomingCustomLanguage=settings.BuildTextCustomLanguage,
            IncomingCustomLanguageCode=settings.BuildTextCustomLanguageCode,
            FastProvider=settings.BuildTextFastProvider=="common"?settings.FastProvider:settings.BuildTextFastProvider
        };
        var quality=settings with {
            Model=string.IsNullOrWhiteSpace(settings.BuildTextModel)?settings.Model:settings.BuildTextModel.Trim(),
            LlmThinkingEnabled=settings.BuildTextThinking??settings.LlmThinkingEnabled,
            LlmMaxTokens=settings.BuildTextMaxTokens
        };
        return new(settings.BuildTextMode,fast,quality);
    }
}

// Only the build-text worker writes this cache. Text is keyed per field so a
// repeated title can be reused even when the annotation or panel changes.
internal sealed record BuildTextCached(string Value,bool Refine);
internal sealed class BuildTextCache {
    private readonly object gate=new();
    private const int Limit=1024;
    internal const int MaxResultLength=12000;
    private readonly string path;
    private readonly Dictionary<string,string> entries=new(StringComparer.Ordinal);
    private readonly string indexPath;
    private readonly Dictionary<string,Dictionary<string,string>> plans=new(StringComparer.Ordinal);

    internal BuildTextCache(string directory) {
        path=Path.Combine(directory,"build-text-cache-v1.json");
        indexPath=Path.Combine(directory,"build-text-plans-v1.json");
        try{
            var file=new FileInfo(path);
            var stored=file.Exists&&file.Length<=64L*1024*1024
                ?JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllBytes(path)):null;
            foreach(var (key,value) in stored?.TakeLast(Limit)??[]){
                if(key.Length==64&&key.All(Uri.IsHexDigit)&&value is {Length:<=MaxResultLength})entries[key]=value;
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){}
        try{
            var file=new FileInfo(indexPath);
            var index=file.Exists&&file.Length<=16L*1024*1024
                ?JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,string>>>(File.ReadAllBytes(indexPath)):null;
            foreach(var (key,fields) in index?.TakeLast(16)??[]){
                if(key.Length!=64||!key.All(Uri.IsHexDigit)||fields==null)continue;
                var valid=fields.Where(item=>item.Key.Length<=6000&&item.Value is {Length:64}&&item.Value.All(Uri.IsHexDigit)).TakeLast(256);
                plans[key]=valid.ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal);
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){}
    }

    internal static string Key(string text, BuildTextProfile profile, bool quality, string glossary, string? context) {
        // Refresh only fields affected by the new tactical guidance. Other cached
        // annotations retain their keys and incur no additional provider request.
        if(quality&&BuildTextLanguage.Hints(text).Length>0)glossary+="\0"+BuildTextLanguage.Revision;
        var fast=profile.Fast;
        var llm=profile.Quality;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
            schema=quality?2:1,text,glossary,quality,context=quality?context:null,language=TranslationLanguages.Incoming(fast),fast.BilingualNames,fast.ChineseNameStyle,
            provider=quality?null:fast.FastProvider,
            llm=quality?new{llm.LlmBaseUrl,llm.Model,llm.LlmThinkingEnabled,llm.LlmThinkingApi,llm.LlmMaxTokens,llm.LlmStreaming}:null
        })));
    }

    internal static string Scope(string build, string context) {
        // Loading order, whitespace, and changing win-rate badges are presentation
        // details. Retain the build name as the namespace for context-sensitive terms.
        if(!string.IsNullOrWhiteSpace(build)){
            var title=System.Text.RegularExpressions.Regex.Replace(build,@"<[^>]*>", "",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            title=System.Text.RegularExpressions.Regex.Replace(title,@"\[[^\[\]\r\n]*%[^\[\]\r\n]*\]", "",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            title=System.Text.RegularExpressions.Regex.Replace(title,@"\s+", " ",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Trim();
            return "build:"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title)));
        }
        var lines=context.Replace("\r","").Split('\n').Select(line=>line.Trim()).Where(line=>line.Length>0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return "context:"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",lines))));
    }

    internal bool TryGet(string key, out string? translation){lock(gate)return entries.TryGetValue(key,out translation);}

    internal static string PlanKey(string scope, BuildTextProfile profile, bool quality, string glossary)=>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope+"\0"+Key("",profile,quality,glossary,quality?scope:null))));

    internal bool Remember(string plan, string text, string key) {
        lock(gate){
        if(!plans.TryGetValue(plan,out var fields)){
            if(plans.Count>=16)plans.Remove(plans.Keys.First());
            fields=new(StringComparer.Ordinal);plans.Add(plan,fields);
        }
        if(fields.TryGetValue(text,out var existing)&&existing==key)return false;
        fields[text]=key;
        if(fields.Count>256)fields.Remove(fields.Keys.First());
        return true;
        }
    }

    internal Dictionary<string,string> Snapshot(string plan) {
        lock(gate){
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!plans.TryGetValue(plan,out var fields))return result;
        var size=0;
        foreach(var (text,key) in fields){
            if(!entries.TryGetValue(key,out var value))continue;
            if(size+text.Length+value.Length>96000)break;
            result[text]=value;size+=text.Length+value.Length;
        }
        return result;
        }
    }

    internal void Store(string key, string translation) {
        lock(gate){
        if(translation.Length>MaxResultLength)return;
        entries[key]=translation;
        if(entries.Count>Limit){
            // Compact so a reused dictionary slot cannot disturb insertion order.
            var recent=entries.Where(x=>x.Key!=key).TakeLast(Limit-1).ToArray();
            entries.Clear();
            foreach(var entry in recent)entries.Add(entry.Key,entry.Value);
            entries.Add(key,translation);
        }
        }
    }
    internal void Flush() {
        lock(gate){
        try{
            ConfigStore.WriteAtomic(path,JsonSerializer.SerializeToUtf8Bytes(entries));
            ConfigStore.WriteAtomic(indexPath,JsonSerializer.SerializeToUtf8Bytes(plans));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
    }
}

public sealed partial class Translators {
    internal async Task<string[]> TranslateBuildFast(string[] texts, Settings settings, CancellationToken ct) {
        var language=TranslationLanguages.Incoming(settings);
        if(texts.Length==1)return [await Fast(texts[0],settings,ct,background:true)];
        if(settings.FastProvider=="azure"){
            using var admission=fastSlots.TryEnterBackground()??throw new FastProviderBusyException();
            ct.ThrowIfCancellationRequested();CheckCooldown("azure");
            var target=TranslationLanguages.RequireServiceCode(language);
            var prepared=texts.Select(text=>language.ChineseGlossary?terms.Protect(text,settings.BilingualNames,true):terms.ProtectOutgoing(text)).ToArray();
            var results=await AzureBatch(prepared.Select(item=>item.Text).ToArray(),settings,ct,target);
            return results.Select((value,index)=>terms.RenderChineseStyle(Terms.Restore(value,prepared[index].Map),texts[index],
                language.ChineseGlossary&&settings.ChineseNameStyle=="legacy")).ToArray();
        }
        // Bing's web endpoint takes one string. Use explicit boundaries and
        // validate the complete result before associating any field with a label.
        var marker="⟦"+Guid.NewGuid().ToString("N")[..8]+"⟧";
        var payload=string.Join("\n"+marker+"\n",texts);
        var translated=await Fast(payload,settings,ct,background:true);
        var values=translated.Split(marker).Select(value=>value.Trim()).ToArray();
        if(values.Length!=texts.Length||values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Fast translator did not preserve batch boundaries");
        return values;
    }
    internal async Task<string[]> TranslateBuildQuality(string[] texts, string context, BuildTextProfile profile, CancellationToken ct,bool partial=false) {
        var settings=profile.Quality;
        var language=TranslationLanguages.Incoming(profile.Fast);
        var key=store.LlmSecretFor(profile.Fast);
        if(key.Length==0)throw new InvalidOperationException("未配置大模型密钥");
        CheckCooldown("llm");
        using var lease=llmSlots.TryEnterIdle()??throw new FastProviderBusyException();
        ct.ThrowIfCancellationRequested();
        var prepared=texts.Select(text=>language.ChineseGlossary?terms.Prepare(text,settings.BilingualNames):terms.PrepareOutgoingNames(text)).ToArray();
        var items=prepared.Select((value,index)=>new{
            id=index,text=Terms.Restore(value.Text,value.Map),
            requiredNames=value.Map.Values.Distinct(),
            glossary=value.Hints.Select(term=>new{term.Source,term.Target,term.Ambiguous}),
            buildTerminology=language.ChineseGlossary?BuildTextLanguage.Hints(texts[index]):[]
        });
        var input=JsonSerializer.Serialize(new{targetLanguage=language.Name,context,items},ConfigStore.Json);
        const string prompt=BuildTextLanguage.Prompt;
        var epoch=Interlocked.Read(ref qualityEpoch);
        var previous=providerIssues.GetValueOrDefault("llm");
        var trace=Telemetry.Begin(settings,false,0);
        TouchModel(settings.Model);
        try{
            using var request=new HttpRequestMessage(HttpMethod.Post,ApiConfigurationDiagnostics.ChatEndpoint(settings.LlmBaseUrl));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
            request.Content=LlmRequestOptions.Create(settings,prompt,input);
            var result=await ReadCompletion(request,epoch,trace,ct,input.Length,maximumCharacters:1000000);
            using var response=JsonDocument.Parse(result);
            if(response.RootElement.ValueKind!=JsonValueKind.Array||!partial&&response.RootElement.GetArrayLength()!=texts.Length)
                throw new InvalidDataException("Incomplete build translation batch");
            var translations=new string[texts.Length];
            foreach(var entry in response.RootElement.EnumerateArray()){
                if(!entry.TryGetProperty("id",out var index)||!index.TryGetInt32(out var id)||
                    !entry.TryGetProperty("translation",out var text)||text.ValueKind!=JsonValueKind.String){
                    if(partial)continue;
                    throw new InvalidDataException("Invalid build translation entry");
                }
                var translation=text.GetString();
                if(id<0||id>=texts.Length||translations[id]!=null||string.IsNullOrWhiteSpace(translation)||translation.Length>BuildTextCache.MaxResultLength)
                { if(partial)continue;throw new InvalidDataException("Invalid build translation entry"); }
                try {
                    translations[id]=terms.RenderChineseStyle(NormalizeNames(translation,prepared[id].Map),texts[id],language.ChineseGlossary&&settings.ChineseNameStyle=="legacy");
                }catch(TranslationOutputException) when(partial){}
            }
            trace.Finish("done");ClearIssue("llm",previous,epoch);
            return translations;
        }catch(OperationCanceledException){trace.Finish("cancelled");throw;}
        catch{trace.Finish("failed");throw;}
        finally{TouchModel(settings.Model);}
    }
}
