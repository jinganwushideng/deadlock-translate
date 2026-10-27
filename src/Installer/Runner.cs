using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate.Setup;
public sealed class Runner(Package package,Action<string> progress,InstallerAudit? audit=null){
    public async Task<JsonElement> Run(Dictionary<string,object?> request,CancellationToken ct=default){
        var id=Guid.NewGuid().ToString("N");var input=Path.Combine(package.Root,id+"-request.json");var output=Path.Combine(package.Root,id+"-result.json");
        request["payload"]=package.Payload;
        var timer=Stopwatch.StartNew();var action=request.GetValueOrDefault("action")?.ToString()??"unknown";
        audit?.Record("step-start",new{action,game=request.GetValueOrDefault("game"),runtime=request.GetValueOrDefault("runtime"),mode=request.GetValueOrDefault("mode")});
        await File.WriteAllTextAsync(input,JsonSerializer.Serialize(request),new UTF8Encoding(true),ct);
        var powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
        var info=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=package.Scripts};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-STA","-ExecutionPolicy","Bypass","-File",Path.Combine(package.Scripts,"Installer-Workflow.ps1"),"-RequestFile",input,"-ResultFile",output})info.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=info};
        process.OutputDataReceived+=(_,e)=>{if(e.Data==null)return;try{var s=JsonSerializer.Deserialize<string>(e.Data);if(s!=null&&s.Length<500)progress(s);}catch{}};
        // Error output may contain command-line or private Steam arguments. Do not log it.
        process.ErrorDataReceived+=(_,_)=>{};
        if(!process.Start())throw new IOException("无法启动 Windows PowerShell。");process.BeginOutputReadLine();process.BeginErrorReadLine();
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
        await process.WaitForExitAsync(ct);process.WaitForExit();
        if(!File.Exists(output)){audit?.Record("step-missing-result",new{action,elapsedMs=timer.ElapsedMilliseconds,exitCode=process.ExitCode});throw new IOException("安装步骤未返回结果，请检查安全软件是否拦截 PowerShell，或导出安装日志。");}
        using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(output,Encoding.UTF8,ct));
        var operations=Path.Combine(package.Root,"operations.jsonl");
        try{if(File.Exists(operations)&&new FileInfo(operations).Length<2*1024*1024){
            foreach(var line in await File.ReadAllLinesAsync(operations,ct))try{
                using var entry=JsonDocument.Parse(line);var root=entry.RootElement;
                if(root.TryGetProperty("stage",out var s)&&s.GetString()?.Length<=80&&root.TryGetProperty("path",out var p)&&p.GetString()?.Length<=1024&&root.TryGetProperty("outcome",out var o))
                    audit?.Record("file-operation",new{operationAt=root.TryGetProperty("at",out var at)&&DateTimeOffset.TryParse(at.GetString(),out var timestamp)?(DateTimeOffset?)timestamp:null,stage=s.GetString(),path=p.GetString(),outcome=o.GetString(),expectedHash=TraceHash(root,"expectedHash"),actualHash=TraceHash(root,"actualHash")});
            }catch(JsonException){}catch(InvalidOperationException){}
            File.Delete(operations);
        }}catch(IOException){audit?.Record("trace-read-failed",new{action});}catch(UnauthorizedAccessException){audit?.Record("trace-read-failed",new{action});}
        if(!doc.RootElement.GetProperty("ok").GetBoolean()){
            if(doc.RootElement.TryGetProperty("failure",out var failure)){audit?.Record("step-failed",new{action,elapsedMs=timer.ElapsedMilliseconds,failure});throw new InstallStepException(failure);}
            throw new IOException(doc.RootElement.GetProperty("error").GetString());
        }
        audit?.Record("step-complete",new{action,elapsedMs=timer.ElapsedMilliseconds});
        var data=doc.RootElement.GetProperty("data");
        if(data.TryGetProperty("gameVpk",out var gameVpk)||data.TryGetProperty("runtimeIntegrity",out _))audit?.Record("integrity-report",new{gameVpk=gameVpk.ValueKind==JsonValueKind.Undefined?(object?)null:gameVpk,runtimeIntegrity=data.TryGetProperty("runtimeIntegrity",out var ri)?ri:(object?)null});
        if(data.TryGetProperty("reinstall",out var reinstall)&&reinstall.ValueKind==JsonValueKind.Object)audit?.Record("reinstall-plan",reinstall);
        if(doc.RootElement.GetProperty("data").TryGetProperty("failures",out var failures)&&failures.ValueKind==JsonValueKind.Array)foreach(var failure in failures.EnumerateArray())audit?.Record("optional-step-failed",new{action,failure});
        return doc.RootElement.GetProperty("data").Clone();
    }
    static string? TraceHash(JsonElement root,string key)=>root.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {Length:64} hash&&hash.All(Uri.IsHexDigit)?hash:null;
}
