using System.Diagnostics;
using System.Text.Json;

namespace DeadlockTranslate;

// All timing is outside Panorama. No game memory, input hooks or translation services here.
public sealed class GameExitDeadline(long started) {
    private bool seen;
    private long? absent;
    public bool ShouldStop(bool? running,long now) {
        if(running is null){absent=null;return false;} // Access errors are not proof of exit.
        if(running.Value){seen=true;absent=null;return false;}
        if(!seen)return now-started>=180_000;
        absent??=now;
        return now-absent.Value>=10_000;
    }
}

public sealed class GameLifecycle(string executable,Action stop) : IAsyncDisposable {
    private readonly object gate=new();
    private readonly CancellationTokenSource cancellation=new();
    private Task? task;
    public bool Active {get{lock(gate)return task!=null;}}
    public void Arm(){lock(gate)task??=Run();}
    private async Task Run(){
        var deadline=new GameExitDeadline(Environment.TickCount64);
        try{while(!cancellation.IsCancellationRequested){
            if(deadline.ShouldStop(GameProcess.IsRunning(executable),Environment.TickCount64)){stop();return;}
            await Task.Delay(2000,cancellation.Token);
        }}catch(OperationCanceledException){}
    }
    public async ValueTask DisposeAsync(){cancellation.Cancel();if(task!=null)await task;cancellation.Dispose();}
}

public static class GameProcess {
    public static string ResolveGame(string dataDir){
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir,"install-manifest.json")));
        var target=doc.RootElement.GetProperty("target").GetString()!;
        var game=Directory.GetParent(Path.GetDirectoryName(target)!)!.Parent!.FullName;
        var executable=Path.GetFullPath(Path.Combine(game,"bin","win64","deadlock.exe"));
        if(!File.Exists(executable))throw new FileNotFoundException("Deadlock executable not found",executable);
        return executable;
    }
    public static bool? IsRunning(string executable){
        if(string.IsNullOrWhiteSpace(executable))return null;
        bool unknown=false;
        var candidates=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try{foreach(var process in candidates){
            try{
                if(string.Equals(process.MainModule?.FileName,executable,StringComparison.OrdinalIgnoreCase))return true;
            }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){unknown=true;}
        }}finally{foreach(var process in candidates)process.Dispose();}
        return unknown?null:false;
    }
}
