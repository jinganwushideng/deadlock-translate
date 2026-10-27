using System.Diagnostics;

namespace DeadlockTranslate;

// Steam expands %command% after --steam-game. Never interpret the game's arguments
// as backend options or put them through cmd.exe / another shell.
public sealed record SteamGameLaunch(string Executable,string[] Arguments,string WorkingDirectory) {
    public static (string[] Backend,SteamGameLaunch? Game) Split(string[] arguments){
        var marker=Array.IndexOf(arguments,"--steam-game");
        if(marker<0)return (arguments,null);
        if(marker+1>=arguments.Length)throw new ArgumentException("Steam 未提供游戏命令；启动选项必须包含 %command%");
        var executable=arguments[marker+1];
        if(!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Steam 游戏程序路径无效");
        return (arguments[..marker],new(Path.GetFullPath(executable),arguments[(marker+2)..],Environment.CurrentDirectory));
    }
    public Process Start(string expectedExecutable){
        if(!string.Equals(Executable,Path.GetFullPath(expectedExecutable),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Steam 游戏路径与安装记录不一致");
        if(string.Equals(Executable,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝递归启动翻译程序");
        if(Arguments==null||Arguments.Length>128||Arguments.Any(a=>a==null||a.Length>16384)||Arguments.Sum(a=>a.Length)>32768)
            throw new ArgumentException("游戏参数长度无效");
        if(!Path.IsPathFullyQualified(WorkingDirectory)||!Directory.Exists(WorkingDirectory))throw new ArgumentException("游戏工作目录无效");
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,WorkingDirectory=WorkingDirectory};
        foreach(var argument in Arguments)info.ArgumentList.Add(argument);
        // Same-user UAC can start via the Windows shell rather than Steam's environment.
        // Deadlock's known application ID is the only default we supply; no arbitrary env is accepted.
        if(!info.Environment.TryGetValue("SteamAppId",out var appId)||string.IsNullOrEmpty(appId))info.Environment["SteamAppId"]="1422450";
        if(!info.Environment.TryGetValue("SteamGameId",out var gameId)||string.IsNullOrEmpty(gameId))info.Environment["SteamGameId"]="1422450";
        var process=Process.Start(info)??throw new InvalidOperationException("游戏启动失败");
        // The translation backend runs below normal priority. Do not transfer that to a PvP game.
        try{process.PriorityClass=ProcessPriorityClass.Normal;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        return process;
    }
}
public sealed record LaunchGameRequest(InstanceScope Scope,SteamGameLaunch Game);
