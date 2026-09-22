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
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,WorkingDirectory=WorkingDirectory};
        foreach(var argument in Arguments)info.ArgumentList.Add(argument);
        var process=Process.Start(info)??throw new InvalidOperationException("游戏启动失败");
        // The translation backend runs below normal priority. Do not transfer that to a PvP game.
        try{process.PriorityClass=ProcessPriorityClass.Normal;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        return process;
    }
}
