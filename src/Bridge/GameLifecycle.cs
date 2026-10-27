using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

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
                var path=ImagePath(process.Id);if(path==null){unknown=true;continue;}
                if(string.Equals(path,executable,StringComparison.OrdinalIgnoreCase))return true;
            }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){unknown=true;}
        }}finally{foreach(var process in candidates)process.Dispose();}
        return unknown?null:false;
    }
    // Limited query rights suffice even when reading modules would require higher rights.
    public static string? ImagePath(int processId){
        var handle=OpenProcess(0x1000,false,processId);if(handle==IntPtr.Zero)return null;
        try{var buffer=new StringBuilder(32768);var size=buffer.Capacity;return QueryFullProcessImageName(handle,0,buffer,ref size)?buffer.ToString():null;}finally{CloseHandle(handle);}
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
