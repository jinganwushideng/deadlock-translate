using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate.Setup;
public sealed class Runner(Package package,Action<string> progress,InstallerAudit? audit=null,Action<string>? waiting=null,Func<CancellationToken>? operationCancellation=null,int readTimeoutMilliseconds=60_000){
    public async Task<JsonElement> Run(Dictionary<string,object?> request,CancellationToken ct=default){
        var id=Guid.NewGuid().ToString("N");var input=Path.Combine(package.Root,id+"-request.json");var output=Path.Combine(package.Root,id+"-result.json");
        request["payload"]=package.Payload;
        var timer=Stopwatch.StartNew();var action=request.GetValueOrDefault("action")?.ToString()??"unknown";
        var writes=action is "apply" or "recover" or "restore-transaction" or "steam-retry";
        var certificate=action is "certificate" or "remove-certificate";
        if(action is "apply" or "recover" or "steam-retry")request["transactionId"]=id;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,operationCancellation?.Invoke()??default);
        ct=linked.Token;
        var signal=writes && request.TryGetValue("state",out var stateValue) && stateValue is string statePath
            ? Path.Combine(statePath,"installer-signals",id+"-cancel") : Path.Combine(package.Root,id+"-cancel");
        var progressFile=Path.Combine(package.Root,id+"-progress.json");
        audit?.Record("step-start",new{action,game=request.GetValueOrDefault("game"),runtime=request.GetValueOrDefault("runtime"),mode=request.GetValueOrDefault("mode")});
        await File.WriteAllTextAsync(input,JsonSerializer.Serialize(request),new UTF8Encoding(true),ct);
        var powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
        var info=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=package.Scripts};
        info.Environment["DLT_INSTALL_CANCEL"]=signal;
        info.Environment["DLT_INSTALL_PROGRESS"]=progressFile;
        info.Environment.Remove("DLT_INSTALL_JOURNAL");
        info.Environment.Remove("DLT_INSTALL_CHILD");
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-STA","-ExecutionPolicy","Bypass","-File",Path.Combine(package.Scripts,"Installer-Workflow.ps1"),"-RequestFile",input,"-ResultFile",output})info.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=info,EnableRaisingEvents=true};
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited+=(_,_)=>exited.TrySetResult();
        string lastStage=action;
        long lastActivity=0;
        process.OutputDataReceived+=(_,e)=>{if(e.Data==null)return;try{var s=JsonSerializer.Deserialize<string>(e.Data);if(s!=null&&s.Length<500){Volatile.Write(ref lastStage,s);Interlocked.Exchange(ref lastActivity,timer.ElapsedMilliseconds);progress(s);}}catch(JsonException){}};
        // Error output may contain command-line or private Steam arguments. Do not log it.
        process.ErrorDataReceived+=(_,_)=>{};
        try{if(!process.Start())throw new IOException("无法启动 Windows PowerShell。");}
        catch(Exception e) when(e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException){audit?.Record("step-start-failed",new{action,type=e.GetType().Name,nativeError=(e as System.ComponentModel.Win32Exception)?.NativeErrorCode});throw new IOException("无法启动 Windows PowerShell。请重试或导出安装日志。",e);}
        process.BeginOutputReadLine();process.BeginErrorReadLine();
        long? cancellationRequested=null;
        bool timedOut=false;
        var progressStamp=DateTime.MinValue;
        while(await Task.WhenAny(exited.Task,Task.Delay(500))!=exited.Task){
            var elapsed=timer.ElapsedMilliseconds;
            try{
                var stamp=File.GetLastWriteTimeUtc(progressFile);
                if(stamp>progressStamp && File.Exists(progressFile)){
                    progressStamp=stamp;
                    using var state=JsonDocument.Parse(File.ReadAllText(progressFile));
                    var stage=state.RootElement.GetProperty("stage").GetString();
                    if(stage is {Length:>0 and <500}){Volatile.Write(ref lastStage,stage);Interlocked.Exchange(ref lastActivity,elapsed);}
                }
            }catch(Exception e) when(e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException){}
            var current=Volatile.Read(ref lastStage);
            var waitingForWindows=certificate||current.Contains("等待 Windows 确认",StringComparison.Ordinal);
            var idle=elapsed-Interlocked.Read(ref lastActivity);
            var expiry=!waitingForWindows && (writes?idle>=300_000:elapsed>=(action=="deep"?65_000:readTimeoutMilliseconds));
            if(cancellationRequested==null&&(ct.IsCancellationRequested||expiry)){
                timedOut=expiry;cancellationRequested=elapsed;
                Package.NoLinks(signal);
                Directory.CreateDirectory(Path.GetDirectoryName(signal)!);
                File.WriteAllText(signal,"");
                audit?.Record(expiry?"step-timeout":"step-cancel-request",new{action,stage=current,elapsedMs=elapsed});
            }
            waiting?.Invoke(current+" · 已用 "+(elapsed/1000)+" 秒"+(cancellationRequested!=null?" · 等待安全停止":waitingForWindows?" · 等待 Windows 确认":idle>=60_000?" · 暂无新进展":" · 上次进展 "+(idle/1000)+" 秒前"));
            if(cancellationRequested!=null&&!waitingForWindows&&elapsed-cancellationRequested.Value>=(writes?20_000:3_000)){
                // Only terminate this exact Process handle. Steam and other user processes are never a kill target.
                try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}
                await Task.WhenAny(exited.Task,Task.Delay(2000));
                audit?.Record("step-worker-stopped",new{action,elapsedMs=elapsed,exitCode=process.HasExited?(int?)process.ExitCode:null});
                if(writes)throw new IOException("工作进程未响应，操作已中止。请重新打开安装器检查并恢复未完成的安装。");
                if(timedOut)throw new TimeoutException("检查超时，可以重新检测或导出日志。");
                throw new OperationCanceledException(ct);
            }
        }
        try{File.Delete(signal);}catch(IOException){}catch(UnauthorizedAccessException){}
        if(!File.Exists(output)){audit?.Record("step-missing-result",new{action,elapsedMs=timer.ElapsedMilliseconds,exitCode=process.ExitCode});throw new IOException("安装步骤退出时没有返回结果。可以重新检测或导出安装日志。");}
        using var doc=await ReadResult(output,action,process.ExitCode);
        var operations=Path.Combine(package.Root,"operations.jsonl");
        try{if(File.Exists(operations)&&new FileInfo(operations).Length<2*1024*1024){
            foreach(var line in await File.ReadAllLinesAsync(operations))try{
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
        if(data.TryGetProperty("migration",out var migration)&&migration.ValueKind==JsonValueKind.Object)audit?.Record("game-migration-plan",migration);
        if(data.TryGetProperty("gameCorrection",out var correction)&&correction.ValueKind==JsonValueKind.Object)audit?.Record("game-location-recovery",correction);
        if(data.TryGetProperty("runtimeChoice",out var runtimeChoice)&&runtimeChoice.ValueKind==JsonValueKind.Object)audit?.Record("runtime-location-choice",runtimeChoice);
        if(data.TryGetProperty("runtimeMove",out var runtimeMove)&&runtimeMove.ValueKind==JsonValueKind.Object)audit?.Record("runtime-relocation-plan",runtimeMove);
        if(doc.RootElement.GetProperty("data").TryGetProperty("failures",out var failures)&&failures.ValueKind==JsonValueKind.Array)foreach(var failure in failures.EnumerateArray())audit?.Record("optional-step-failed",new{action,failure});
        return doc.RootElement.GetProperty("data").Clone();
    }
    async Task<JsonDocument> ReadResult(string path,string action,int exitCode){
        try{
            if(new FileInfo(path).Length>8*1024*1024)throw new JsonException();
            var result=JsonDocument.Parse(await File.ReadAllTextAsync(path,Encoding.UTF8));
            var root=result.RootElement;
            if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("ok",out var ok)&&
                ((ok.ValueKind==JsonValueKind.True&&root.TryGetProperty("data",out _))||
                 (ok.ValueKind==JsonValueKind.False&&(root.TryGetProperty("failure",out _)||root.TryGetProperty("error",out _)))))return result;
            result.Dispose();throw new JsonException();
        }catch(JsonException e){audit?.Record("step-invalid-result",new{action,exitCode});throw new IOException("安装步骤返回的结果损坏。请重新检测或导出日志。",e);}
    }
    static string? TraceHash(JsonElement root,string key)=>root.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {Length:64} hash&&hash.All(Uri.IsHexDigit)?hash:null;
}
