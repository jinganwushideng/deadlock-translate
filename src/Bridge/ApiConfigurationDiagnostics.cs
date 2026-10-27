using System.Net;
using System.Text.Json;

namespace DeadlockTranslate;

public sealed record ApiConfigurationInput(string Provider,string? BaseUrl=null,string? Model=null,string? Key=null,string? Region=null,bool UseSavedKey=true,int? MaxTokens=null,bool? ThinkingEnabled=null,string? ThinkingApi=null,bool? Streaming=null);
public sealed record ApiFieldCheck(string Field,string Level,string Code,string Message);
public sealed record ApiConfigurationReport(string Provider,bool Ready,string? Endpoint,string? ModelId,string KeySource,ApiFieldCheck[] Checks);
internal sealed record ResolvedApiConfiguration(ApiConfigurationReport Report,string Key,string Region,int MaxTokens=4096,bool ThinkingEnabled=true,string ThinkingApi="auto",bool Streaming=true);
public sealed record ApiFailureDiagnosis(string Code,string Field,string Title,string Detail,string[] Steps,LogReason Reason);
public sealed record ApiConnectionReport(ApiConfigurationReport Configuration,bool Attempted,string Outcome,string Code,string Field,string Title,string Detail,string[] Steps,int? HttpStatus=null,double? ElapsedMs=null);

public static class ApiConfigurationDiagnostics
{
    public static string ChatEndpoint(string baseUrl)
    {
        var value=baseUrl.Trim().TrimEnd('/');
        return value.EndsWith("/chat/completions",StringComparison.Ordinal)?value:value+"/chat/completions";
    }
    public static ApiConfigurationReport Inspect(ConfigStore store,ApiConfigurationInput input)=>Resolve(store,input).Report;
    internal static ResolvedApiConfiguration Resolve(ConfigStore store,ApiConfigurationInput input)
    {
        var checks=new List<ApiFieldCheck>();
        void Add(string field,string level,string code,string text)=>checks.Add(new(field,level,code,text));
        var provider=input.Provider??"";
        if(provider is not ("llm" or "azure"))return new(new(provider,false,null,null,"missing",[new("provider","error","provider-invalid","请选择大模型或 Azure API。")]),"","");
        var current=store.Current;
        var maximumTokens=input.MaxTokens??current.LlmMaxTokens;
        var thinkingEnabled=input.ThinkingEnabled??current.LlmThinkingEnabled;var thinkingApi=input.ThinkingApi??current.LlmThinkingApi;var streaming=input.Streaming??current.LlmStreaming;
        var draft=input.Key??"";
        var key=draft.Trim();var keySource=key.Length>0?"input":input.UseSavedKey?"saved":"missing";
        if(key.Length==0)key=input.UseSavedKey?store.Secret(provider):"";
        if(key.Length==0){keySource="missing";Add("key","error","key-missing","未输入或保存该服务的 API 密钥。留空只会沿用已保存密钥，不会自动生成。");}
        else if(key.Length>8192||key.Any(c=>!char.IsAscii(c)||char.IsWhiteSpace(c)||char.IsControl(c))||key.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))Add("key","error","key-format","密钥含空白/隐藏字符、非 ASCII 字符，或复制了 Bearer 前缀。只填写密钥本身，不能填完整请求头。");
        else Add("key","ok","key-present",keySource=="saved"?"使用已保存密钥；格式可用，真实有效性需要联网测试。":"使用当前输入密钥；格式可用，真实有效性需要联网测试。");

        string? endpoint=null,model=null;var region=input.Region??current.AzureRegion;
        if(provider=="llm")
        {
            if(!LlmRequestOptions.ValidBudget(maximumTokens))Add("tokens","error","token-budget-invalid","模型输出上限须在 512 到 16384 token 之间，思考也会消耗该额度。");
            else Add("tokens","ok","token-budget-valid","输出上限包括思考和最终译文；上限不是固定扣费数。");
            if(!LlmRequestOptions.ValidThinkingApi(thinkingApi))Add("thinking","error","thinking-api-invalid","请选择自动识别、DeepSeek 兼容格式或服务默认。");
            var address=input.BaseUrl??current.LlmBaseUrl;
            if(keySource=="saved"&&key.Length>0&&Uri.TryCreate(address?.Trim(),UriKind.Absolute,out var draftUrl)&&Uri.TryCreate(current.LlmBaseUrl,UriKind.Absolute,out var savedUrl)&&draftUrl.GetLeftPart(UriPartial.Authority)!=savedUrl.GetLeftPart(UriPartial.Authority))Add("key","error","key-service-changed","地址已换到另一服务，不能默认沿用保存的密钥测试。请输入这个服务对应的密钥。");
            if(string.IsNullOrWhiteSpace(address))Add("address","error","address-missing","API 地址不能为空，请填写服务商提供的 BaseURL。");
            else if(address.Length>2048||address.Trim().Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||c is '\u200b' or '\ufeff')||address.Trim().EndsWith("复制",StringComparison.Ordinal)||!Uri.TryCreate(address.Trim(),UriKind.Absolute,out var uri))Add("address","error","address-format","API 地址不是有效的完整网址，或包含空白、隐藏字符或“复制”等页面文字。请使用 https:// 开头的接口地址。");
            else if(uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback))Add("address","error","address-scheme","远程 API 必须使用 HTTPS；仅本机接口允许 HTTP。");
            else if(uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)Add("address","error","address-credentials","地址不能包含用户名、密钥、查询参数或 # 片段。密钥应放在独立输入框。");
            else
            {
                var path=uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Any(p=>p is "login" or "signin" or "dashboard" or "console" or "docs" or "documentation" or "api-keys"))Add("address","error","address-webpage","地址看起来是登录页、控制台或文档页，不能作为 API BaseURL。");
                else if(parts.Any(p=>p is "anthropic" or "responses" or "messages" or "models"))Add("address","error","address-protocol","当前翻译器使用 Chat Completions 协议；请换对应的 BaseURL，不要填 Anthropic、Responses、Messages 或模型列表地址。");
                else if(parts.Count(p=>p=="chat")>1||parts.Count(p=>p=="completions")>1)Add("address","error","address-duplicate","地址中重复填写了 chat/completions。可填 BaseURL，或完整的 Chat Completions 端点。");
                else
                {
                    endpoint=ChatEndpoint(address);
                    Add("address","ok","address-format-ok","地址格式可用，下面显示的是程序实际发送请求的端点。是否存在需要联网测试。");
                    if(path==""||path=="/")Add("address","warning","address-root","仅填写了域名，程序会追加 /chat/completions。部分服务还需要 /v1 或其他路径，请按服务商说明核对。");
                    if(path.EndsWith("/chat/completions")&&!uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions",StringComparison.Ordinal))Add("address","warning","address-case","接口路径大小写可能不正确。服务端可能区分大小写，请使用完整、准确的端点。");
                }
            }
            model=input.Model??current.Model;
            if(!string.IsNullOrEmpty(model)&&(model==key||model.StartsWith("sk-",StringComparison.OrdinalIgnoreCase)&&model.Length>20)){model=null;Add("model","error","model-looks-like-key","模型栏看起来填了 API 密钥。密钥应放入独立的密钥框，模型栏填写完整模型 ID。");}
            else if(string.IsNullOrWhiteSpace(model)||model.Length>150)Add("model","error","model-missing","模型 ID 不能为空或过长；请完整填写服务商给出的接口模型 ID。");
            else if(model!=model.Trim()||model.Any(c=>char.IsControl(c)||c is '\u200b' or '\ufeff' or '\u00a0'))Add("model","error","model-hidden-space","模型 ID 含前后空格、换行或隐藏字符；必须一字不差地填写。");
            else
            {
                Add("model","ok","model-format-ok","模型名格式可用；名称是否存在、该密钥是否有使用权限，需要联网测试。");
                if(model.Any(char.IsWhiteSpace))Add("model","warning","model-display-name","模型 ID 包含空格，可能复制了展示名。请核对接口要求的完整 ID 和大小写。");
            }
        }
        else
        {
            endpoint="https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans";
            Add("address","ok","azure-endpoint","Azure 使用程序固定的正式 Translator 端点。");
            if(region.Length>80||region!=region.Trim()||region.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='-')))Add("region","error","region-format","区域应是资源的准确区域代码，例如 eastasia，不能包含空格或隐藏字符。");
            else if(region.Length==0)Add("region","warning","region-missing","区域为空。部分单服务资源允许留空，区域型/多服务资源通常需要准确区域；以联网测试为准。");
            else Add("region","ok","region-format-ok","区域代码格式可用；是否与密钥资源一致需要联网测试。");
        }
        if(provider=="llm"&&endpoint!=null&&model!=null&&LlmRequestOptions.ValidThinkingApi(thinkingApi)){
            var applied=LlmRequestOptions.AppliedThinking(endpoint,model,thinkingApi,thinkingEnabled);
            Add("thinking",applied.HasValue&&thinkingApi!="deepseek"?"ok":"warning","thinking-policy",applied.HasValue?(applied.Value?"本次将发送开启思考参数。":"本次将发送关闭思考参数。")+"第三方接口须支持 thinking.type；不支持时改为服务默认。":"自动模式没有识别此接口的思考协议，本次不发送开关，开关效果取决于服务默认。若服务文档支持 thinking.type，可选 DeepSeek 兼容格式后测试。");
            Add("streaming","ok","streaming-policy",streaming?"请求流式响应以显示阶段；如果接口返回普通 JSON，将在同一次请求中解析。参数不被支持时请关闭流式后主动测试。":"本次使用完整 JSON 响应，通常只能看到等待和完成状态。");
        }
        return new(new(provider,!checks.Any(c=>c.Level=="error"),endpoint,model,keySource,checks.ToArray()),key,region,maximumTokens,thinkingEnabled,thinkingApi,streaming);
    }

    public static ApiFailureDiagnosis HttpFailure(string provider,HttpStatusCode status,string? body)
    {
        string code="",type="",message="",parameter="";
        if(body is {Length:<=65536})try
        {
            using var doc=JsonDocument.Parse(body);var root=doc.RootElement;
            if(root.ValueKind==JsonValueKind.Object)
            {
                var error=root.TryGetProperty("error",out var child)?child:root;
                string Read(string key)=>error.ValueKind==JsonValueKind.Object&&error.TryGetProperty(key,out var v)&&v.ValueKind is JsonValueKind.String or JsonValueKind.Number?v.ToString():"";
                code=Read("code").ToLowerInvariant();type=Read("type").ToLowerInvariant();message=Read("message").ToLowerInvariant();parameter=Read("param").ToLowerInvariant();
            }
        }catch(JsonException){}
        bool Has(params string[] terms)=>terms.Any(t=>code.Contains(t)||type.Contains(t)||message.Contains(t));
        if(Translators.IsRedirect(status))return new("redirect","address","API 地址返回了跳转","地址可能指向网页入口；程序不会把密钥转交给跳转目标。",["填写服务商的正式 Chat Completions BaseURL，不能使用登录页或控制台地址。","核对地址后再次测试。"],LogReason.ServiceRedirect);
        if(status==HttpStatusCode.Unauthorized)return new("authentication","key","API 密钥鉴权失败","密钥无效、过期、复制错误，或与当前服务/套餐接口不对应。",["仅填写密钥本身，确认属于这个 BaseURL 和套餐；不要复制 Bearer 前缀。","确认密钥未撤销，必要时在服务商控制台重新生成。"],LogReason.Unauthorized);
        if(status==HttpStatusCode.PaymentRequired||code=="403001"||Has("insufficient_quota","quota_exceeded","billing_hard_limit","insufficient balance","insufficient_balance","quota has been exceeded","exceeded its free quota"))return new("quota","account","账户额度或余额不足","服务返回了额度/余额相关错误；这与 Windows 管理员权限无关。",["检查对应 API 账号、套餐额度、账单和项目限制。","额度恢复后再测试；不要反复更换游戏权限。"],LogReason.QuotaExceeded);
        if(Has("model_not_found","unknown_model","invalid_model")||((parameter=="model"||message.Contains("model"))&&Has("does not exist","not found","not available","no access")))return new("model","model","模型不存在或当前密钥无权使用","服务明确指向模型名称或模型授权问题。",["复制接口要求的完整模型 ID，核对大小写、日期/后缀以及隐藏空格。","ID 正确时检查该套餐/项目是否有此模型的使用权限。"],LogReason.ModelUnavailable);
        if(status==HttpStatusCode.Unauthorized||Has("invalid_api_key","authentication_error","incorrect api key"))return new("authentication","key","API 密钥鉴权失败","密钥无效、过期、复制错误，或与当前服务/套餐接口不对应。",["仅填写密钥本身，确认属于这个 BaseURL 和套餐；不要复制 Bearer 前缀。","确认密钥未撤销，必要时在服务商控制台重新生成。"],LogReason.Unauthorized);
        if(status==HttpStatusCode.Forbidden&&provider=="azure"&&message.Contains("region"))return new("region","region","Azure 区域或资源权限不匹配","服务提示需要检查 Translator 资源区域。",["填写密钥对应资源的准确区域，核对资源类型为 Translator。"],LogReason.RegionMismatch);
        if(status==HttpStatusCode.Forbidden)return new("permission","account","服务拒绝访问","可能缺少模型/项目/套餐权限，或存在资源区域和访问限制；仅凭 403 不能认定密钥错误。",["检查服务账号授权、模型可用范围、套餐接口与资源区域。","若权限正确，向服务商提供脱敏后的错误类型和 HTTP 状态。"],LogReason.Unauthorized);
        if(status==HttpStatusCode.NotFound)return new("not-found","address","接口路径或模型未找到（HTTP 404）","服务未给出足够信息，暂不能确定是地址还是模型 ID。",["核对下方实际请求端点，与服务商的 Chat Completions 示例一致。","再核对完整模型 ID 和套餐权限，不要仅修改其中一项后就认为已修好。"],LogReason.EndpointInvalid);
        if(status==HttpStatusCode.TooManyRequests)return new("rate-limit","concurrency","服务请求限流","当前请求频率或并发超过服务限制。",["等待限流恢复，降低该服务并发，检查套餐限制。"],LogReason.RateLimit);
        if(provider=="llm"&&(status==HttpStatusCode.BadRequest||(int)status==422)&&Has("unsupported","not supported","unknown parameter","invalid parameter")){
            if(parameter.Contains("thinking")||message.Contains("thinking"))return new("thinking-unsupported","thinking","接口不支持当前思考参数","思考开关参数未被服务接受。",["核对服务文档；可选择服务默认或正确的思考接口格式，再主动测试。"],LogReason.ParametersRejected);
            if(parameter.Contains("stream")||message.Contains("stream"))return new("streaming-unsupported","streaming","接口不支持当前流式参数","流式参数未被服务接受。",["关闭流式阶段反馈后主动测试，不会自动重新调用付费接口。"],LogReason.ParametersRejected);
        }
        if(status==HttpStatusCode.BadRequest||(int)status==422||status==HttpStatusCode.MethodNotAllowed||status==HttpStatusCode.UnsupportedMediaType)return new("parameters","address","接口拒绝请求协议或参数","地址/模型可能使用了另一种协议，或不接受当前 Chat Completions 参数。",["确认使用 Chat Completions 兼容接口，并核对模型的参数要求。","模型 ID 与地址均正确时，检查该供应商的兼容性说明。"],LogReason.ParametersRejected);
        if((int)status>=500)return new("server","service","翻译服务暂不可用","服务端返回 HTTP "+(int)status+"，不一定是输入配置错误。",["稍后重试，查看服务商状态；持续出现时联系服务商。"],LogReason.Network);
        return new("http","service","翻译服务返回 HTTP "+(int)status,"目前无法从响应中可靠定位到某个配置字段。",["核对配置后导出脱敏诊断日志，联系服务商或项目维护者。"],LogReason.Other);
    }
    public static ApiFailureDiagnosis NetworkFailure(Exception ex)=>ex switch
    {
        OperationCanceledException=>new("timeout","network","连接测试超时","排队或服务响应超过测试时限。",["检查服务网络、并发和响应速度；不要直接认定模型名错误。"],LogReason.Timeout),
        HttpRequestException {HttpRequestError:HttpRequestError.NameResolutionError}=>new("dns","address","无法解析 API 域名","域名拼写或网络 DNS 可能有问题。",["检查 API 域名拼写、DNS 与网络环境。"],LogReason.Network),
        HttpRequestException {HttpRequestError:HttpRequestError.SecureConnectionError}=>new("tls","network","API HTTPS 连接验证失败","远程证书、系统时间或网络代理可能影响 TLS。",["核对 Windows 时间、服务证书和代理配置；不要关闭证书验证。"],LogReason.Network),
        HttpRequestException {HttpRequestError:HttpRequestError.ProxyTunnelError}=>new("proxy","network","API 代理隧道连接失败","代理未能连接远程 API，尚不能判断密钥或模型是否正确。",["检查代理的连通性、规则和服务地址；连接恢复后重试。"],LogReason.Network),
        HttpRequestException error when ProviderNetworkFailure.SocketCode(error)==System.Net.Sockets.SocketError.TimedOut=>new("connect-timeout","network","API 网络连接超时","连接或读取远程服务时超时，并非模型输出超时；修改输出 token 上限通常无效。",["检查服务地址、网络与代理；服务恢复后重试，原文已保留。"],LogReason.Timeout),
        HttpRequestException {HttpRequestError:HttpRequestError.ConnectionError}=>new("connection","network","API 网络连接中断","远程服务不可达或连接被关闭；无法仅凭此结果认定密钥或模型有误。",["检查服务网络和代理，使用“翻译服务”连接测试核对；不要重复安装 Mod 或证书。"],LogReason.Network),
        HttpRequestException {HttpRequestError:HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse}=>new("response-ended","network","API 响应传输中断","服务已开始响应，但没有传回完整译文；为避免重复调用，没有自动重放请求。",["检查网络、代理或服务状态后主动重试；原文已保留。"],LogReason.Network),
        _=>new("network","network","无法连接翻译 API","无法仅凭此结果判断密钥或模型是否正确。",["检查实际请求地址、网络和代理，确认服务可达后再测试鉴权与模型。"],LogReason.Network)
    };
    internal static async Task<string?> ReadErrorBody(HttpResponseMessage response,CancellationToken ct)
    {
        if(response.Content.Headers.ContentLength>65536)return null;
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var bytes=new byte[4096];
        while(buffer.Length<=65536){var count=await stream.ReadAsync(bytes,ct);if(count==0)return System.Text.Encoding.UTF8.GetString(buffer.ToArray());buffer.Write(bytes,0,count);}
        return null;
    }
}
