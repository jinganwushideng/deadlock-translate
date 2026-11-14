using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using System.Text.Json;

namespace DeadlockTranslate;

public sealed class StartupFault(string code,string message) : Exception(message) {
    public string Code {get;}=code;
}

// Elevation is opt-in. Never retry runas just because a foreground game is elevated.
public static class StartupCompatibility {
    public static string Owner()=>Hash(WindowsIdentity.GetCurrent().User?.Value??throw new StartupFault("identity","无法确认当前 Windows 用户。"));
    public static string Scope(string directory)=>Hash(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant());
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static bool Elevated()=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool? ProcessElevated(int processId){
        var process=OpenProcess(0x1000,false,processId);if(process==IntPtr.Zero)return null;
        try{if(!OpenProcessToken(process,8,out var token))return null;try{return GetTokenInformation(token,20,out var value,4,out _)?value!=0:null;}finally{CloseHandle(token);}}finally{CloseHandle(process);}
    }
    public static bool? SameUser(int processId){
        var process=OpenProcess(0x1000,false,processId);if(process==IntPtr.Zero)return null;
        try{
            if(!OpenProcessToken(process,8,out var token))return null;
            try{using var target=new WindowsIdentity(token);using var current=WindowsIdentity.GetCurrent();return target.User==null?null:target.User==current.User;}
            catch(Exception ex) when(ex is ArgumentException or System.Security.SecurityException or Win32Exception){return null;}
            finally{CloseHandle(token);}
        }finally{CloseHandle(process);}
    }
    public static bool RequiresElevation(bool requested,bool elevated)=>requested&&!elevated;
    public static bool CheckSteamContext(bool administratorCompatibility=false){
        bool? sameUser=null,elevated=null;
        try{
            using var active=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if(active?.GetValue("pid") is int pid&&pid>0){
                using var steam=Process.GetProcessById(pid);
                using var launcher=Process.GetCurrentProcess();
                // The registry can outlive Steam; PID reuse or a different session
                // isn't proof of a mismatch. Only reject a known live client.
                if(!steam.HasExited&&steam.SessionId==launcher.SessionId&&steam.ProcessName.Equals("steam",StringComparison.OrdinalIgnoreCase)){
                    sameUser=SameUser(pid);
                    elevated=ProcessElevated(pid);
                }
            }
        }catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException){}
        return ValidateSteamContext(sameUser,elevated,Elevated(),administratorCompatibility);
    }
    public static bool ValidateSteamContext(bool? sameUser,bool? steamElevated,bool launcherElevated,bool administratorCompatibility=false){
        if(sameUser==false)throw new StartupFault("steam-user-mismatch","Steam 和当前启动器使用了不同的 Windows 用户。请完整退出 Steam，以当前 Windows 用户重新打开 Steam 后启动游戏。");
        if(steamElevated.HasValue&&steamElevated.Value!=launcherElevated){
            if(administratorCompatibility&&launcherElevated)return false;
            throw new StartupFault("steam-permission-mismatch","Steam 和当前启动器的管理员权限不一致，继续启动可能导致游戏无法连接 Steam 并退出。请完整退出 Steam，取消 Steam、Deadlock 和翻译程序属性中的「以管理员身份运行」，再从 Steam 库启动。");
        }
        return sameUser==true&&steamElevated.HasValue;
    }
    // Only documented launch identifiers cross UAC. Never copy PATH, credentials
    // or a process's complete environment into command-line arguments.
    private static readonly string[] SteamVariables=["SteamAppId","SteamGameId","SteamOverlayGameId","SteamClientLaunch","SteamEnv","SteamTenfoot"];
    public static string CaptureSteamEnvironment()=>Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
        SteamVariables.Select(name=>(name,value:Environment.GetEnvironmentVariable(name)))
            .Where(item=>ValidSteamValue(item.value)).ToDictionary(item=>item.name,item=>item.value!)));
    private static bool ValidSteamValue(string? value)=>value is {Length:>0 and <=80}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    public static void RestoreSteamEnvironment(string[] arguments){
        var index=Array.IndexOf(arguments,"--steam-launch-context");
        if(index<0)return;
        try{
            if(index+1>=arguments.Length||arguments[index+1].Length>2048)throw new FormatException();
            var values=JsonSerializer.Deserialize<Dictionary<string,string>>(Convert.FromBase64String(arguments[index+1]))??throw new FormatException();
            if(values.Any(item=>!SteamVariables.Contains(item.Key,StringComparer.Ordinal)||!ValidSteamValue(item.Value)))throw new FormatException();
            foreach(var (name,value) in values)Environment.SetEnvironmentVariable(name,value);
        }catch(Exception ex) when(ex is FormatException or JsonException or ArgumentException){
            throw new StartupFault("steam-context-invalid","Steam 管理员兼容启动信息无效，请重新从 Steam 库启动。");
        }
    }
    public static void ValidateOwner(string? requested,string current){
        if(requested!=null && (requested.Length!=64 || !requested.All(Uri.IsHexDigit) || !string.Equals(requested,current,StringComparison.Ordinal)))
            throw new StartupFault("different-user","管理员兼容模式必须使用原来的 Windows 用户确认 UAC。请勿输入另一个管理员账户，否则证书和 DPAPI 密钥不属于同一用户。没有修改证书或配置。");
    }
    public static ProcessStartInfo ElevationInfo(string executable,string[] arguments,string dataDirectory,string owner){
        var marker=Array.IndexOf(arguments,"--steam-game");var backend=marker<0?arguments:arguments[..marker];
        if(backend.Contains("--elevation-owner"))throw new StartupFault("elevation-loop","后台仍未获得管理员权限；已停止重复请求 UAC。");
        var info=new ProcessStartInfo(executable){UseShellExecute=true,Verb="runas",WorkingDirectory=Environment.CurrentDirectory};
        foreach(var arg in backend)info.ArgumentList.Add(arg);
        if(!backend.Contains("--admin-compatible"))info.ArgumentList.Add("--admin-compatible");
        // Pin the initiating user's data location before Windows changes the environment.
        if(!backend.Contains("--data-dir")){info.ArgumentList.Add("--data-dir");info.ArgumentList.Add(Path.GetFullPath(dataDirectory));}
        info.ArgumentList.Add("--elevation-owner");info.ArgumentList.Add(owner);
        if(marker>=0){
            var context=CaptureSteamEnvironment();
            // An earlier value is never replayed over this launcher's fresh context.
            var old=info.ArgumentList.IndexOf("--steam-launch-context");
            if(old>=0){info.ArgumentList.RemoveAt(old);if(old<info.ArgumentList.Count)info.ArgumentList.RemoveAt(old);}
            info.ArgumentList.Add("--steam-launch-context");info.ArgumentList.Add(context);
        }
        if(marker>=0)foreach(var arg in arguments[marker..])info.ArgumentList.Add(arg);
        return info;
    }
    public static string LaunchOption(string executable,bool administratorCompatibility=false)=>'"'+executable+'"'+(administratorCompatibility?" --admin-compatible":"")+" --steam-game %command%";
    public static bool ConfirmAdminGame()=>MessageBox(IntPtr.Zero,
        "Windows 已确认这个游戏要求管理员权限。是否以管理员兼容模式继续？\n\n选择「是」后由 Windows 请求 UAC，只能使用当前 Windows 用户。以后可在安装器「启动项管理」勾选管理员兼容。\n\n普通游戏保留普通模式；若管理员模式中游戏提示 Steam 未运行，请让 Steam 和游戏使用相同的权限再试。",
        "同频 · 游戏需要管理员权限",0x24)==6;
    public static string Message(Exception exception)=>exception switch {
        StartupFault fault=>fault.Message,
        Win32Exception {NativeErrorCode:1223}=>"已取消管理员权限确认，翻译后台没有启动。可使用普通 Launch.cmd，或重新选择管理员兼容启动。",
        Win32Exception {NativeErrorCode:740}=>"Windows 确认 Deadlock 需要管理员权限。确实需要时，请在安装器「启动项管理」勾选「管理员兼容启动」，或将 --admin-compatible 放到 Steam 启动项的 --steam-game 前，并用原 Windows 用户确认 UAC。若是误设管理员属性，可取消 deadlock.exe 的强制管理员运行后按普通模式启动。",
        UnauthorizedAccessException=>"无法访问翻译程序或用户数据目录。请使用安装时的 Windows 用户，检查目录权限；不要复制或删除加密密钥来解决。",
        FileNotFoundException=>"安装记录或程序文件缺失。请重新运行 Install.cmd 或成套覆盖更新；后台与游戏 VPK 必须来自同一包。",
        System.Security.Cryptography.CryptographicException=>"当前 Windows 用户无法读取加密配置或证书。请使用原安装用户启动，避免在 UAC 输入另一个管理员账户；不要删除密钥或重复安装证书。可运行 Export-Diagnostics.cmd 导出日志。",
        ArgumentException=>"启动参数或安装记录无效。请检查 Steam 启动项：英文双引号包住程序完整路径，后接 --steam-game %command%。",
        _=>"翻译后台启动失败。请运行 Export-Diagnostics.cmd 导出日志，检查证书、端口和安装记录。"
    };
    public static LogReason Reason(Exception exception)=>exception switch {
        Win32Exception {NativeErrorCode:1223}=>LogReason.UacCancelled,
        Win32Exception {NativeErrorCode:740}=>LogReason.ElevationRequired,
        StartupFault {Code:"pairing-missing"}=>LogReason.PairingMissing,
        StartupFault {Code:"instance-mismatch" or "version-mismatch"}=>LogReason.InstanceMismatch,
        StartupFault {Code:"scope-mismatch"}=>LogReason.ScopeMismatch,
        StartupFault {Code:"port-busy"}=>LogReason.PortBusy,
        StartupFault {Code:"port-timeout"}=>LogReason.PortTimeout,
        StartupFault {Code:"different-user" or "steam-user-mismatch"}=>LogReason.DifferentUser,
        StartupFault {Code:"steam-permission-mismatch"}=>LogReason.SteamPermissionMismatch,
        StartupFault {Code:"handoff-timeout"}=>LogReason.HandoffTimeout,
        StartupFault {Code:"elevation-required" or "elevation-loop" or "admin-unavailable"}=>LogReason.ElevationRequired,
        UnauthorizedAccessException=>LogReason.Unauthorized,
        _=>LogReason.Other
    };
    public static void Notify(Exception exception,bool noShell){
        if(!noShell)MessageBox(IntPtr.Zero,Message(exception),"同频 · 启动未完成",0x10);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBox(IntPtr window,string text,string caption,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(IntPtr token,int information,out int value,int bytes,out int required);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
