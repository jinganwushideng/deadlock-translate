using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;

namespace DeadlockTranslate;

public sealed record InstanceScope(string Owner,string Data,string Runtime);
public sealed record InstanceInfo(string Version,bool Elevated,bool Handoff);

public static class InstanceCoordinator {
    public static bool Matches(InstanceScope? requested,InstanceScope expected)=>requested!=null&&requested==expected;
    public static async Task LaunchGame(HttpClient http,string origin,string pairing,InstanceScope scope,SteamGameLaunch game){
        using var response=await http.PostAsJsonAsync(origin+"/control/"+pairing+"/launch-game",new LaunchGameRequest(scope,game));
        if(response.StatusCode==HttpStatusCode.Conflict)throw new System.ComponentModel.Win32Exception(740);
        response.EnsureSuccessStatusCode();
    }
    // Check the authenticated instance before touching certificates or binding a second listener.
    public static bool IsListening(int port){try{return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint=>endpoint.Port==port);}catch(NetworkInformationException){return true;}}
    public static async Task<InstanceInfo?> Prepare(HttpClient http,string origin,string pairing,InstanceScope scope,bool requireAdmin,bool currentElevated,Func<int,bool>? isListening=null){
        isListening??=IsListening;
        if(!isListening(new Uri(origin).Port))return null;
        using var probe=await Probe(http,origin+"/health");
        if(probe==null)return null;
        if(!probe.IsSuccessStatusCode)throw new StartupFault("port-busy","本地端口被其他程序占用。没有替换证书或关闭其他程序，请导出日志检查端口。");
        Health? health;
        try{health=await probe.Content.ReadFromJsonAsync<Health>();}catch(Exception ex) when(ex is System.Text.Json.JsonException or NotSupportedException){throw new StartupFault("port-busy","本地端口响应不是翻译后台。请检查端口占用。");}
        if(health?.App!="DeadlockTranslate")throw new StartupFault("port-busy","本地端口被其他程序占用，请检查端口占用。");
        using var response=await http.PostAsJsonAsync(origin+"/control/"+pairing+"/instance",scope);
        if(response.StatusCode==HttpStatusCode.NotFound)throw new StartupFault("instance-mismatch","已经有旧版或另一份翻译后台运行，但与当前安装包不匹配。请先在旧后台设置页停止后台，再启动当前安装目录；不要重新安装证书。");
        if(response.StatusCode==HttpStatusCode.Conflict)throw new StartupFault("scope-mismatch","已有后台属于另一 Windows 用户、数据目录或安装目录。请退出那份后台并使用原安装目录启动；不会跨用户接管或复制证书。");
        response.EnsureSuccessStatusCode();
        var instance=await response.Content.ReadFromJsonAsync<InstanceInfo>()??throw new StartupFault("instance-mismatch","无法确认已运行后台的状态。");
        if(instance.Version!=BuildInfo.Version)throw new StartupFault("version-mismatch","已运行后台版本与安装包不同。请退出旧后台后成套更新后台与 VPK。");
        if(!requireAdmin||instance.Elevated)return instance;
        if(!currentElevated||!instance.Handoff)throw new StartupFault("admin-unavailable","现有后台仍为普通权限，请停止它后使用管理员兼容启动。");
        using var stop=await http.PostAsJsonAsync(origin+"/control/"+pairing+"/admin-handoff",scope);
        stop.EnsureSuccessStatusCode();
        var deadline=Environment.TickCount64+10000;
        do {
            await Task.Delay(200);
            if(!isListening(new Uri(origin).Port))return null;
            using var ready=await Probe(http,origin+"/health");
            if(ready==null)return null;
        } while(Environment.TickCount64<deadline);
        throw new StartupFault("handoff-timeout","旧后台未在规定时间内退出。没有强制关闭它，请在设置页停止后台后重试。");
    }
    private sealed record Health(string App);
    private static async Task<HttpResponseMessage?> Probe(HttpClient http,string url){
        try{return await http.GetAsync(url);}catch(HttpRequestException ex) when(ex.HttpRequestError==HttpRequestError.ConnectionError){return null;}
        catch(TaskCanceledException){throw new StartupFault("port-timeout","本地端口有响应超时，暂时无法确认旧后台。请检查端口占用或停止旧后台后重试。");}
    }
}
