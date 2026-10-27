using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DeadlockTranslate;

public sealed partial class Translators
{
    readonly SemaphoreSlim apiProbeGate=new(1,1);
    public async Task<ApiConnectionReport> TestApiConfiguration(ApiConfigurationInput input,DiagnosticLog? log=null,CancellationToken cancellation=default)
    {
        var resolved=ApiConfigurationDiagnostics.Resolve(store,input);var report=resolved.Report;
        if(!report.Ready)return new(report,false,"blocked","local-input",report.Checks.First(c=>c.Level=="error").Field,"请先修正输入配置","本地检查未通过，没有调用云端服务。",["按下方各字段提示修改，再测试连接。"]);
        if(!await apiProbeGate.WaitAsync(0,cancellation))return new(report,false,"busy","busy","service","已有 API 测试正在进行","等待当前测试结束后重试，没有重复调用服务。",[]);
        var clock=Stopwatch.StartNew();var attempted=false;
        var draft=store.Current with{LlmBaseUrl=report.Endpoint??store.Current.LlmBaseUrl,Model=report.ModelId??store.Current.Model,LlmMaxTokens=resolved.MaxTokens,LlmThinkingEnabled=resolved.ThinkingEnabled,LlmThinkingApi=resolved.ThinkingApi,LlmStreaming=resolved.Streaming};
        var trace=report.Provider=="llm"?Telemetry.Begin(draft,false,0,true):null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(store.Current.LlmTimeoutSeconds,5,30)));
            using var admission=await EnterProvider(report.Provider,timeout.Token,true);
            using var request=new HttpRequestMessage(HttpMethod.Post,report.Endpoint);
            if(report.Provider=="llm")
            {
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",resolved.Key);
                request.Content=LlmRequestOptions.Create(draft,"This is a connection diagnostic. Reply with the word OK only.","Connection test. Reply OK.");
            }
            else
            {
                request.Headers.Add("Ocp-Apim-Subscription-Key",resolved.Key);
                if(resolved.Region.Length>0)request.Headers.Add("Ocp-Apim-Subscription-Region",resolved.Region);
                request.Content=JsonContent.Create(new[]{new{Text="Connection test."}});
            }
            trace?.Stage("waiting");attempted=true;using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode){var errorBody=await ApiConfigurationDiagnostics.ReadErrorBody(response,timeout.Token);ApplyApiCooldown(response,report.Provider);return Failed(ApiConfigurationDiagnostics.HttpFailure(report.Provider,response.StatusCode,errorBody),(int)response.StatusCode);}
            try
            {
                string? text;
                if(report.Provider=="llm")
                {
                    text=await ChatCompletionReader.Read(response,trace!,timeout.Token);
                }
                else {var body=await ApiConfigurationDiagnostics.ReadErrorBody(response,timeout.Token);if(body==null)throw new JsonException();using var json=JsonDocument.Parse(body);text=json.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();}
                if(string.IsNullOrWhiteSpace(text))throw new JsonException();
                trace?.Finish("done");
                log?.Record("translation",LogKind.TranslationCompleted,new(Provider:report.Provider=="llm"?"quality":"azure",Stage:"api-config-test",HttpStatus:(int)response.StatusCode,ElapsedMs:clock.ElapsedMilliseconds));
                return new(report,true,"ok","ok","service","API 连接测试通过","固定诊断请求已得到可用文本。本次通过不保证后续额度、网络和翻译质量。",["当前输入没有自动保存；确认后点击“保存设置”应用。"],(int)response.StatusCode,clock.ElapsedMilliseconds);
            }
            catch(TranslationOutputException ex){return Failed(new(ex.Reason==LogReason.ReasoningBudget?"reasoning-budget":"output-truncated","tokens","模型未产生完整测试译文",ex.Message,["调整输出上限或思考开关，再主动测试；当前输入未保存。"],ex.Reason),(int)response.StatusCode,"warning");}
            catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
            {
                var steps=report.Provider=="llm"?new[]{"核对实际请求端点，使用 Chat Completions 兼容协议。","不要使用 Responses 或 Anthropic 入口；接口协议正确时检查模型的输出格式要求。"}:new[]{"核对 Azure Text Translator 资源和密钥类型，不能使用 Speech 或其他服务的密钥。","服务返回的内容与 Translator V3 不匹配，确认网络代理没有替换响应。"};
                return Failed(new("protocol","address","服务响应格式与翻译器不兼容","服务虽返回成功状态，但没有提供当前协议需要的文本。可能填了网页入口或其他 API 协议。",steps,LogReason.InvalidData),(int)response.StatusCode);
            }
        }
        catch(InvalidOperationException)
        {
            return Failed(new("rate-limit","concurrency","服务暂处于限流冷却","已有请求收到限流，测试会遵守同一服务的冷却与并发限制。",["等待冷却结束后重试，适当降低服务并发。"],LogReason.RateLimit),null);
        }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return Failed(ApiConfigurationDiagnostics.NetworkFailure(ex),null);
        }
        finally{trace?.Finish(cancellation.IsCancellationRequested?"cancelled":"failed");apiProbeGate.Release();}
        ApiConnectionReport Failed(ApiFailureDiagnosis failure,int? status,string outcome="failed")
        {
            log?.Record("translation",LogKind.TranslationFailed,new(Reason:failure.Reason,Provider:report.Provider=="llm"?"quality":"azure",Stage:"api-config-test",HttpStatus:status,ElapsedMs:clock.ElapsedMilliseconds));
            return new(report,attempted,outcome,failure.Code,failure.Field,failure.Title,failure.Detail,failure.Steps,status,clock.ElapsedMilliseconds);
        }
    }
    async Task CheckApiResponse(HttpResponseMessage response,string provider,string stage,long epoch,CancellationToken ct)
    {
        if(response.IsSuccessStatusCode)return;
        ApplyApiCooldown(response,provider);
        string? body=null;
        try{body=await ApiConfigurationDiagnostics.ReadErrorBody(response,ct);}catch(IOException){}
        var diagnosis=ApiConfigurationDiagnostics.HttpFailure(provider,response.StatusCode,body);
        throw Failure(response.StatusCode,provider,stage,epoch,diagnosis.Title+"："+diagnosis.Detail+" "+string.Join(" ",diagnosis.Steps),diagnosis.Reason);
    }
    void ApplyApiCooldown(HttpResponseMessage response,string provider)
    {
        if(response.StatusCode!=HttpStatusCode.TooManyRequests)return;
        var retry=response.Headers.RetryAfter;var delay=retry?.Delta??(retry?.Date-DateTimeOffset.UtcNow)??TimeSpan.FromSeconds(30);
        var until=Environment.TickCount64+(long)Math.Max(1000,delay.TotalMilliseconds);blockedUntil.AddOrUpdate(provider,until,(_,old)=>Math.Max(old,until));
    }
}
