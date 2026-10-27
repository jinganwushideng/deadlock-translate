using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

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
        if(marker>=0)foreach(var arg in arguments[marker..])info.ArgumentList.Add(arg);
        return info;
    }
    public static string LaunchOption(string executable,bool admin)=>'"'+executable+'"'+(admin?" --admin-compatible":"")+" --steam-game %command%";
    public static string Message(Exception exception)=>exception switch {
        StartupFault fault=>fault.Message,
        Win32Exception {NativeErrorCode:1223}=>"已取消管理员权限确认，翻译后台没有启动。可使用普通 Launch.cmd，或重新选择管理员兼容启动。",
        Win32Exception {NativeErrorCode:740}=>"Deadlock 要求管理员权限，但翻译后台以普通权限启动。请使用 Launch-Admin.cmd，或在 Steam 翻译启动项中、--steam-game 前添加 --admin-compatible；需由你确认 UAC。也可以取消游戏的管理员兼容设置。",
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
        StartupFault {Code:"different-user"}=>LogReason.DifferentUser,
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
