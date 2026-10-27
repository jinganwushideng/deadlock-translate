using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;
public interface ITranslator {
    Task<string> Fast(string text, Settings config, CancellationToken ct);
    Task<string> Quality(string text, string[] context, Settings config, CancellationToken ct);
}
public sealed partial class Translators : ITranslator, IDisposable {
    private readonly ConfigStore store;private readonly Terms terms;private readonly HttpClient http;
    private readonly AsyncConcurrency fastSlots;private readonly PriorityConcurrency llmSlots;
    private readonly TranslationMemo qualityMemo=new();private long qualityEpoch;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> blockedUntil=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,ProviderIssue> providerIssues=new();
    public ProviderIssue[] Issues=>providerIssues.Values.OrderBy(x=>x.Provider).ToArray();
    private readonly DiagnosticLog? log;
    public LlmTelemetry Telemetry {get;}
    public Translators(ConfigStore store,Terms terms,HttpClient http,DiagnosticLog? log=null){this.store=store;this.terms=terms;this.http=http;this.log=log;Telemetry=new(log);fastSlots=new(store.Current.FastConcurrency);llmSlots=new(store.Current.LlmConcurrency,store.Current.OutgoingEnabled&&store.Current.ReserveSendSlot);store.Changed+=UpdateLimits;}
    private void UpdateLimits(){CancelKeepAlive();fastSlots.SetLimit(store.Current.FastConcurrency);llmSlots.Configure(store.Current.LlmConcurrency,store.Current.OutgoingEnabled&&store.Current.ReserveSendSlot);Interlocked.Increment(ref qualityEpoch);qualityMemo.Clear();providerIssues.Clear();}
    public object Stats=>new{fast=fastSlots.Stats,llm=llmSlots.Stats,qualityCache=qualityMemo.Stats,recent=Telemetry.Recent};
    private void CheckCooldown(string provider){if(blockedUntil.TryGetValue(provider,out var until)&&until>Environment.TickCount64)throw new InvalidOperationException("服务限流冷却中，请稍后重试");}
    private async Task<IDisposable> EnterProvider(string provider,CancellationToken ct,bool priority=false,string? model=null){
        CheckCooldown(provider);var lease=provider=="llm"?await EnterRealLlm(ct,priority,model):await fastSlots.Enter(ct);
        try{ct.ThrowIfCancellationRequested();CheckCooldown(provider);return lease;}catch{lease.Dispose();throw;}
    }
    public void Dispose(){CancelKeepAlive();store.Changed-=UpdateLimits;qualityMemo.Dispose();apiProbeGate.Dispose();}

    private readonly SemaphoreSlim bingLock = new(1);
    private sealed record BingCredentials(string Key,string Token,string Ig,Uri Origin,DateTime Until);
    private BingCredentials? bing;
    public async Task<string> Fast(string text, Settings config, CancellationToken ct) {
        var language=TranslationLanguages.Incoming(config);
        var exact = terms.Exact(text,config.BilingualNames,language.ChineseGlossary);
        if(exact != null) return terms.RenderChineseStyle(exact,text,language.ChineseGlossary&&config.ChineseNameStyle=="legacy");
        var protectedText = language.ChineseGlossary?terms.Protect(text,config.BilingualNames,true):terms.ProtectOutgoing(text);
        var target=TranslationLanguages.RequireServiceCode(language);
        using var admission=await EnterProvider(config.FastProvider,ct);
        string result = config.FastProvider == "azure" ? await Azure(protectedText.Text,config,ct,target) : await Bing(protectedText.Text,ct,target);
        return terms.RenderChineseStyle(Terms.Restore(result, protectedText.Map),text,language.ChineseGlossary&&config.ChineseNameStyle=="legacy");
    }
    private async Task<string> Azure(string text, Settings config, CancellationToken ct,string target="zh-Hans") {
        var epoch=Interlocked.Read(ref qualityEpoch);var previous=providerIssues.GetValueOrDefault("azure");
        var key = store.Secret("azure"); if(key.Length == 0) throw new InvalidOperationException("未配置 Azure 密钥");
        using var req = new HttpRequestMessage(HttpMethod.Post,"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to="+target);
        req.Headers.Add("Ocp-Apim-Subscription-Key",key);
        if(config.AzureRegion.Length>0) req.Headers.Add("Ocp-Apim-Subscription-Region", config.AzureRegion);
        req.Content = JsonContent.Create(new[]{new {Text=text}});
        using var res = await http.SendAsync(req,ct); await CheckApiResponse(res,"azure","azure-translate",epoch,ct);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var result=json.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";ClearIssue("azure",previous,epoch);return result;
    }
    private async Task<BingCredentials> BingToken(CancellationToken ct,Uri? preferredOrigin=null,long epoch=-1){
        var cached=Volatile.Read(ref bing);
        if(cached!=null && cached.Until>DateTime.UtcNow&&(preferredOrigin==null||cached.Origin==preferredOrigin))return cached;
        await bingLock.WaitAsync(ct);
        try{
            cached=Volatile.Read(ref bing);
            if(cached!=null && cached.Until>DateTime.UtcNow&&(preferredOrigin==null||cached.Origin==preferredOrigin))return cached;
            var uri=new Uri((preferredOrigin?.AbsoluteUri.TrimEnd('/')??"https://www.bing.com")+"/translator?dlt="+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            string html="";Uri? origin=null;var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(var hops=0;hops<=3;hops++){
                if(!visited.Add(uri.AbsoluteUri))throw Failure(HttpStatusCode.Redirect,"bing","bing-page",epoch,"Bing 官方页面跳转循环，请稍后重试或手动配置其他翻译服务");
                using var req=new HttpRequestMessage(HttpMethod.Get,uri);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/139.0.0.0 Safari/537.36");
                req.Headers.CacheControl=new CacheControlHeaderValue{NoCache=true};
                using var page=await http.SendAsync(req,ct);
                if(IsRedirect(page.StatusCode)){
                    if(hops==3||!BingRedirect(uri,page.Headers.Location,false,out var next))throw Failure(page.StatusCode,"bing","bing-page",epoch,"Bing 网页入口返回 HTTP "+(int)page.StatusCode+" 跳转，未能进入支持的官方翻译页面；请在同一台电脑的 Bing 网页实际试译，或手动配置其他服务");
                    uri=next!;continue;
                }
                Check(page,"bing","bing-page",epoch);html=await page.Content.ReadAsStringAsync(ct);origin=new Uri(uri.GetLeftPart(UriPartial.Authority));break;
            }
            var helper=Regex.Match(html,@"params_AbusePreventionHelper\s*=\s*(\[[^\]]+\])",RegexOptions.None,TimeSpan.FromSeconds(1));
            var ig=Regex.Match(html,"IG:\"([^\"]+)\"",RegexOptions.None,TimeSpan.FromSeconds(1));
            if(!helper.Success || !ig.Success)throw Failure(HttpStatusCode.OK,"bing","bing-page",epoch,"Bing 网页未返回可用翻译凭据，可能需要验证或网页协议已变化；请在网页实际试译，或手动配置其他服务",LogReason.InvalidData);
            try{
                using var data=JsonDocument.Parse(helper.Groups[1].Value);
                if(data.RootElement.ValueKind!=JsonValueKind.Array||data.RootElement.GetArrayLength()<2||data.RootElement[1].ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(data.RootElement[1].GetString()))throw new JsonException();
                cached=new(data.RootElement[0].ToString(),data.RootElement[1].GetString()!,ig.Groups[1].Value,origin!,DateTime.UtcNow.AddMinutes(10));
            }catch(JsonException){throw Failure(HttpStatusCode.OK,"bing","bing-page",epoch,"Bing 网页翻译凭据格式已变化，请在网页实际试译，或手动配置其他服务",LogReason.InvalidData);}
            Volatile.Write(ref bing,cached);return cached;
        }finally{bingLock.Release();}
    }
    private async Task<string> Bing(string text,CancellationToken ct,string target="zh-Hans"){
        var epoch=Interlocked.Read(ref qualityEpoch);var previous=providerIssues.GetValueOrDefault("bing");
        var credentials=await BingToken(ct,epoch:epoch);
        // Only credential refresh is serialized. Independent translation POSTs run concurrently.
        for(var attempt=0;attempt<2;attempt++){
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(credentials.Origin,"/ttranslatev3?isVertical=1&IG="+Uri.EscapeDataString(credentials.Ig)+"&IID=translator.5028.1"));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/139.0.0.0 Safari/537.36");
        request.Headers.Referrer=new Uri(credentials.Origin,"/translator");request.Headers.Add("Origin",credentials.Origin.GetLeftPart(UriPartial.Authority));
        request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"fromLang","auto-detect"},{"to",target},{"text",text},{"token",credentials.Token},{"key",credentials.Key}});
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode && response.StatusCode!=HttpStatusCode.TooManyRequests)Interlocked.CompareExchange(ref bing,null,credentials);
        if(IsRedirect(response.StatusCode)){
            if(attempt==0&&BingRedirect(request.RequestUri!,response.Headers.Location,true,out var next)){
                // Never replay a token/POST at Location or turn the POST into a GET.
                // Fetch fresh page credentials at the validated official origin first.
                credentials=await BingToken(ct,new Uri(next!.GetLeftPart(UriPartial.Authority)),epoch);continue;
            }
            throw Failure(response.StatusCode,"bing","bing-translate",epoch,"Bing 翻译接口返回 HTTP "+(int)response.StatusCode+" 跳转；网页可打开不代表免密钥接口可用。请在网页实际试译，检查是否要求验证，或手动配置其他服务");
        }
        Check(response,"bing","bing-translate",epoch);
        string result;
        try{
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()==0||root[0].ValueKind!=JsonValueKind.Object||!root[0].TryGetProperty("translations",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()==0||list[0].ValueKind!=JsonValueKind.Object||!list[0].TryGetProperty("text",out var value)||value.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(value.GetString()))throw new JsonException();
            result=value.GetString()!;
        }catch(JsonException){Interlocked.CompareExchange(ref bing,null,credentials);throw Failure(response.StatusCode,"bing","bing-translate",epoch,"Bing 返回了非翻译内容，可能是验证页面、限流或网页协议变化；请在网页实际试译，或手动配置其他服务",LogReason.InvalidData);}
        ClearIssue("bing",previous,epoch);return result;
        }
        throw new InvalidOperationException("Bing 跳转重试未完成");
    }
    public static bool IsRedirect(HttpStatusCode code)=>(int)code is 301 or 302 or 303 or 307 or 308;
    public static bool BingRedirect(Uri source,Uri? location,bool translation,out Uri? destination){
        destination=null;if(location==null)return false;
        try{var uri=location.IsAbsoluteUri?location:new Uri(source,location);var path=uri.AbsolutePath.TrimEnd('/');
            if(uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Host is not ("www.bing.com" or "cn.bing.com" or "bing.com")||!(path.Equals("/translator",StringComparison.OrdinalIgnoreCase)||translation&&path.Equals("/ttranslatev3",StringComparison.OrdinalIgnoreCase)))return false;
            destination=uri;return true;
        }catch(UriFormatException){return false;}
    }
    private ProviderRequestException Failure(HttpStatusCode status,string provider,string stage,long epoch,string? message=null,LogReason? reasonOverride=null){
        var reason=reasonOverride??(IsRedirect(status)?LogReason.ServiceRedirect:status==HttpStatusCode.TooManyRequests?LogReason.RateLimit:status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden?LogReason.Unauthorized:LogReason.Network);
        if(epoch<0||epoch==Interlocked.Read(ref qualityEpoch))providerIssues[provider]=new(provider,stage,(int)status,reason.ToString(),DateTime.UtcNow);
        return new(provider,stage,message??(IsRedirect(status)?"翻译 API 返回 HTTP "+(int)status+" 跳转，请核对接口 BaseURL，不能填写登录页或控制台地址":status switch{HttpStatusCode.TooManyRequests=>"服务限流，请稍后重试",HttpStatusCode.Unauthorized=>"密钥无效或已过期",HttpStatusCode.Forbidden=>"服务拒绝访问",_=>"翻译服务 HTTP "+(int)status}),status,reason);
    }
    private void ClearIssue(string provider,ProviderIssue? previous,long epoch){if(previous!=null&&epoch==Interlocked.Read(ref qualityEpoch))providerIssues.TryRemove(new KeyValuePair<string,ProviderIssue>(provider,previous));}
    private string MemoKey(string direction,string text,string[] context,Settings config,int? termRevision=null) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new{direction,text,context,outgoingTone=direction=="en"?config.OutgoingTone:"none",customTone=direction=="en"&&config.OutgoingTone=="custom"?config.OutgoingCustomTone:"",incomingTone=direction=="zh"?config.IncomingTone:"none",incomingCustomTone=direction=="zh"&&config.IncomingTone=="custom"?config.IncomingCustomTone:"",config.LlmBaseUrl,effectiveModel=LlmProfiles.Effective(config,direction=="en").Model,effectiveThinking=LlmProfiles.Effective(config,direction=="en").LlmThinkingEnabled,config.Model,config.LlmMaxTokens,config.LlmThinkingEnabled,config.LlmThinkingApi,config.LlmStreaming,config.BilingualNames,config.ChineseNameStyle,config.LlmTimeoutSeconds,incoming=TranslationLanguages.Incoming(config),outgoing=TranslationLanguages.Outgoing(config),Revision=termRevision??terms.Revision,epoch=Interlocked.Read(ref qualityEpoch)}))));
    public Task<string> Quality(string text,string[] context,Settings config,CancellationToken ct) {
        var language=TranslationLanguages.Incoming(config);
        var exact=terms.Exact(text,config.BilingualNames,language.ChineseGlossary);if(exact!=null)return Task.FromResult(terms.RenderChineseStyle(exact,text,language.ChineseGlossary&&config.ChineseNameStyle=="legacy"));
        if(store.LlmSecretFor(config).Length==0)throw new InvalidOperationException("未配置大模型密钥");
        return qualityMemo.Get(MemoKey("zh",text,context,config),TimeSpan.FromSeconds(config.LlmTimeoutSeconds),token=>QualityUncached(text,context,config,token),ct);
    }
    private async Task<string> QualityUncached(string text, string[] context, Settings config, CancellationToken ct) {
        var epoch=Interlocked.Read(ref qualityEpoch);var previous=providerIssues.GetValueOrDefault("llm");
        var language=TranslationLanguages.Incoming(config);
        var exact=terms.Exact(text,config.BilingualNames,language.ChineseGlossary);
        if(exact!=null) return terms.RenderChineseStyle(exact,text,language.ChineseGlossary&&config.ChineseNameStyle=="legacy"); // A curated exact phrase does not need another cloud request.
        var key = store.LlmSecretFor(config); if(key.Length == 0) throw new InvalidOperationException("未配置大模型密钥");
        var prepareStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var protectedText=language.ChineseGlossary?terms.Prepare(text,config.BilingualNames):terms.PrepareOutgoingNames(text);
        var names=protectedText.Map.ToDictionary(x=>x.Key,x=>x.Value.Split('（')[0]);
        var current=Terms.Restore(protectedText.Text,names);
        // Preserve the distinction between starting an objective and delivering its final hit.
        current=Regex.Replace(current,@"\bfinish(?: off)? (?:the )?(?:mid )?boss\b","land the killing blow on the boss",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));
        var endpoint = ApiConfigurationDiagnostics.ChatEndpoint(config.LlmBaseUrl);
        using var req = new HttpRequestMessage(HttpMethod.Post,endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",key);
        // This JSON is text seen by the model. Keep Chinese readable, rather than asking the model to decode \u escapes.
        var modelInput=TranslationPrompt.Build(current,names.Values,protectedText.Hints,language,context,IncomingStyle.Preference(config));
        var effective=LlmProfiles.Effective(config,false);
        var trace=Telemetry.Begin(effective,false,context.Length);Timing("term-prepare",prepareStarted,modelInput.Length,false,trace.Id);
        try{using var admission=await EnterQualityTimed(ct,modelInput.Length,false,trace.Id,effective.Model);
            req.Content=LlmRequestOptions.Create(effective,IncomingStyle.SystemPrompt(config,language.ChineseGlossary),modelInput);
            var textResult=await ReadCompletion(req,epoch,trace,ct,modelInput.Length);
            var result=terms.RenderChineseStyle(NormalizeNames(textResult,protectedText.Map),text,language.ChineseGlossary&&config.ChineseNameStyle=="legacy");trace.Finish("done");ClearIssue("llm",previous,epoch);return result;
        }catch(OperationCanceledException){trace.Finish("cancelled");throw;}catch{trace.Finish("failed");throw;}
    }
    private static string NormalizeNames(string text,Dictionary<string,string> names){try{return Terms.NormalizeQualityNames(text,names);}catch(InvalidDataException){throw new TranslationOutputException(TranslationOutputFailure.NamesMissing);}}
    public Task<string> TranslateOutgoing(string text,Settings config,bool forceFast,CancellationToken ct)=>TranslateOutgoing(text,[],config,forceFast,ct);
    public async Task<string> TranslateOutgoing(string text,string[] context,Settings config,bool forceFast,CancellationToken ct) =>
        OutgoingStyle.ApplyCase(await TranslateOutgoingRaw(text,context,config,forceFast,ct),config);
    private Task<string> TranslateOutgoingRaw(string text,string[] context,Settings config,bool forceFast,CancellationToken ct) {
        context=config.OutgoingUseContext?context.TakeLast(config.OutgoingContextCount).Where(x=>x.Length<=1024).ToArray():[];
        ct.ThrowIfCancellationRequested();
        var prepareStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var timingToken=TimingToken();
        var prepared=terms.PrepareOutgoingNames(text);
        var namesOnly=TranslationPrompt.NameList(prepared,TranslationLanguages.Outgoing(config).Id=="en");
        if(namesOnly!=null){Timing("names-local",prepareStarted,prepared.Map.Count,true,timingToken);return Task.FromResult(namesOnly);}
        var quality=!forceFast&&(config.OutgoingMode=="quality"||(config.OutgoingMode=="auto"&&store.Secret("llm").Length>0));
        if(!quality)return TranslateOutgoingUncached(text,context,config,true,ct,prepared,prepareStarted,timingToken);
        if(store.LlmSecretFor(config).Length==0)throw new InvalidOperationException("未配置大模型密钥");
        return qualityMemo.Get(MemoKey("en",text,context,config,prepared.Revision),TimeSpan.FromSeconds(config.LlmTimeoutSeconds),token=>TranslateOutgoingUncached(text,context,config,false,token,prepared,prepareStarted,timingToken),ct);
    }
    private async Task<string> TranslateOutgoingUncached(string text,string[] context,Settings config,bool forceFast,CancellationToken ct,PreparedTermText protectedText,long prepareStarted,string? timingToken) {
        var epoch=Interlocked.Read(ref qualityEpoch);var previous=providerIssues.GetValueOrDefault("llm");
        var language=TranslationLanguages.Outgoing(config);
        var source=Terms.Restore(protectedText.Text,protectedText.Map);
        var quality=!forceFast && (config.OutgoingMode=="quality" || (config.OutgoingMode=="auto" && store.Secret("llm").Length>0));
        if(!quality){using var fastAdmission=await EnterProvider(config.FastProvider,ct,true);var target=TranslationLanguages.RequireServiceCode(language);var translated=config.FastProvider=="azure"?await Azure(protectedText.Text,config,ct,target):await Bing(protectedText.Text,ct,target);return Terms.Restore(translated,protectedText.Map);}
        var key=store.LlmSecretFor(config);if(key.Length==0)throw new InvalidOperationException("未配置大模型密钥");
        var endpoint=ApiConfigurationDiagnostics.ChatEndpoint(config.LlmBaseUrl);
        var body=TranslationPrompt.Build(source,protectedText.Map.Values,protectedText.Hints,language,context.Length>0?context:null,config.OutgoingTone=="custom"?config.OutgoingCustomTone:null);
        var effective=LlmProfiles.Effective(config,true);
        for(var attempt=0;attempt<2;attempt++){
            var trace=Telemetry.Begin(effective,true,context.Length);
            if(attempt==0)Timing("term-prepare",prepareStarted,body.Length,true,trace.Id);
            else log?.Record("translation",LogKind.TranslationTiming,new(Token:trace.Id,Stage:"names-repair",Provider:"quality",Count:1,Flag:true));
            using var req=new HttpRequestMessage(HttpMethod.Post,endpoint);req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
            try{using var admission=await EnterQualityTimed(ct,body.Length,true,trace.Id,effective.Model);
                var system=OutgoingStyle.SystemPrompt(config)+(attempt==0?"":" A prior attempt omitted a required name. Translate the original current again and include every requiredNames entry verbatim. Do not replace names with pronouns, synonyms, or a different item. Do not add facts.");
                req.Content=LlmRequestOptions.Create(effective,system,body);
                var reply=await ReadCompletion(req,epoch,trace,ct,body.Length);
                string result;
                try{result=terms.NormalizeOutgoingQualityNames(reply,protectedText.Map);}
                catch(InvalidDataException){throw new TranslationOutputException(TranslationOutputFailure.NamesMissing);}
                trace.Finish("done");ClearIssue("llm",previous,epoch);return result;
            }catch(TranslationOutputException ex) when(ex.Reason==LogReason.NameMismatch&&attempt==0&&!ct.IsCancellationRequested){trace.Finish("failed");}
            catch(OperationCanceledException){trace.Finish("cancelled");throw;}catch{trace.Finish("failed");throw;}
        }
        throw new TranslationOutputException(TranslationOutputFailure.NamesMissing);
    }
    private void Check(HttpResponseMessage response,string provider,string stage="provider-request",long epoch=-1) {
        if(response.StatusCode==HttpStatusCode.TooManyRequests){
            var retry=response.Headers.RetryAfter;var delay=retry?.Delta??(retry?.Date-DateTimeOffset.UtcNow)??TimeSpan.FromSeconds(30);
            var until=Environment.TickCount64+(long)Math.Max(1000,delay.TotalMilliseconds);
            blockedUntil.AddOrUpdate(provider,until,(_,old)=>Math.Max(old,until));
        }
        if(response.IsSuccessStatusCode) return;
        throw Failure(response.StatusCode,provider,stage,epoch);
    }
}
