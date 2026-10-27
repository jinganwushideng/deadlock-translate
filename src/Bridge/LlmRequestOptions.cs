using System.Net.Http.Json;

namespace DeadlockTranslate;
public static class LlmRequestOptions {
    public static bool ValidBudget(int tokens)=>tokens is >=512 and <=16384;
    public static bool ValidThinkingApi(string? api)=>api is "auto" or "deepseek" or "provider";
    public static bool? AppliedThinking(string? endpoint,string model,string api,bool enabled){
        if(api=="deepseek")return enabled;
        if(api=="auto"&&Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.IsDefaultPort&&uri.Host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase)&&(model is "deepseek-flash" or "deepseek-v4-pro"))return enabled;
        return null;
    }
    public static HttpContent Create(string model,string instructions,string input,int maximumTokens=4096,bool streaming=false,bool thinkingEnabled=true,string thinkingApi="provider",string? endpoint=null){
        if(!ValidBudget(maximumTokens))throw new ArgumentException("模型输出上限须在 512 到 16384 token 之间");
        if(!ValidThinkingApi(thinkingApi))throw new ArgumentException("无效的思考接口格式");
        var body=new Dictionary<string,object>{["model"]=model,["stream"]=streaming,["temperature"]=0.1,["max_tokens"]=maximumTokens,["messages"]=new[]{new{role="system",content=instructions},new{role="user",content=input}}};
        if(streaming)body["stream_options"]=new{include_usage=true};
        var thinking=AppliedThinking(endpoint,model,thinkingApi,thinkingEnabled);
        if(thinking.HasValue)body["thinking"]=new{type=thinking.Value?"enabled":"disabled"};
        return JsonContent.Create(body);
    }
    public static HttpContent Create(Settings settings,string instructions,string input)=>Create(settings.Model,instructions,input,settings.LlmMaxTokens,settings.LlmStreaming,settings.LlmThinkingEnabled,settings.LlmThinkingApi,settings.LlmBaseUrl);
}
