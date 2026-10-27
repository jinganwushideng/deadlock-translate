using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

public static class DiagnosticExport {
    private static readonly SemaphoreSlim Gate=new(1,1);
    public static async Task<string> Create(string stateDirectory,DiagnosticLog log,object status,Settings? settings,CancellationToken cancellation=default){
        if(!await Gate.WaitAsync(0,cancellation))throw new IOException("Diagnostic export busy");
        try{
        var directory=Path.Combine(stateDirectory,"diagnostics");
        if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Diagnostic directory is a link");
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"DeadlockTranslate-diagnostics-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8]+".zip");
        var snapshots=await log.SnapshotAsync(cancellation);
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var zip=new ZipArchive(file,ZipArchiveMode.Create)){
            static void Text(ZipArchive z,string name,string text){using var output=new StreamWriter(z.CreateEntry(name).Open(),new UTF8Encoding(false));output.Write(text);}
            Text(zip,"README.txt","诊断包只含本程序结构化日志、连接与快捷键状态、已筛选配置。\n不含聊天正文、译文、用户名、API Key、接口地址、原始设置文件、DPAPI 文件、证书私钥或整份游戏控制台日志。\n日志可能包含匿名任务关联码、时间、版本、错误码、耗时和快捷键配置。\n日志停止/磁盘失败后的缺口会在 storage 字段说明；此包不能证明游戏 UI/FPS 验收。气泡/扫描计数每 30 秒和切会话/正常退出时汇总；maxScanMs 为适配器回调历史峰值，不能当作游戏帧耗时。普通主键修饰键不匹配汇总记录，不算发送失败。自定义语气和快捷短语正文不导出。\n");
            Text(zip,"status.json",JsonSerializer.Serialize(new{version=BuildInfo.Version,buildId=BuildInfo.Id,at=DateTime.UtcNow,status,storage=log.Status,os=Environment.OSVersion.VersionString,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()},ConfigStore.Json));
            if(settings!=null)Text(zip,"settings-summary.json",JsonSerializer.Serialize(new{settings.Mode,settings.FastProvider,settings.ChineseNameStyle,settings.OutgoingEnabled,settings.OutgoingHotkey,settings.OutgoingMode,settings.OutgoingDisplayMode,OutgoingTone=OutgoingStyle.Tones.Contains(settings.OutgoingTone)?settings.OutgoingTone:"unknown",OutgoingEnglishCase=OutgoingStyle.LetterCases.Contains(settings.OutgoingEnglishCase)?settings.OutgoingEnglishCase:"unknown",hasCustomTone=!string.IsNullOrWhiteSpace(settings.OutgoingCustomTone),IncomingTone=IncomingStyle.Tones.Contains(settings.IncomingTone)?settings.IncomingTone:"unknown",hasIncomingCustomTone=!string.IsNullOrWhiteSpace(settings.IncomingCustomTone),settings.IncomingShowOwnTranslations,settings.IncomingTargetLanguage,settings.OutgoingTargetLanguage,settings.FastTimeoutSeconds,settings.LlmTimeoutSeconds,settings.LlmMaxTokens,settings.LlmThinkingEnabled,settings.LlmIndependentProfiles,settings.IncomingThinking,settings.OutgoingThinking,hasIncomingModel=!string.IsNullOrWhiteSpace(settings.IncomingModel),hasOutgoingModel=!string.IsNullOrWhiteSpace(settings.OutgoingModel),settings.OutgoingSeparateBilingual,settings.OutgoingSplitLong,quickMessageCount=settings.QuickMessages.Length,quickMessageEnabled=settings.QuickMessages.Count(q=>q.Enabled),settings.LlmThinkingApi,settings.LlmStreaming,settings.LlmKeepAliveEnabled,settings.LlmKeepAliveSeconds,settings.OutgoingTimeoutSeconds,settings.OutgoingUseContext,settings.OutgoingContextCount,settings.OutgoingContextSeconds,settings.FastConcurrency,settings.LlmConcurrency,settings.OutgoingConcurrency,settings.OutgoingQueueLimit,settings.SettingsBackgroundMode,settings.IncomingDisplayMode,settings.IncomingFeedSeconds,settings.IncomingFeedLines,settings.IncomingShowHeroName,settings.IncomingShowAccountName,settings.IncomingHeroNamePosition,settings.IncomingOpacity,settings.IncomingFeedWidth,settings.IncomingFeedPosition,settings.IncomingFeedOffsetX,settings.IncomingFeedOffsetY,settings.ScanIntervalMs,settings.Transport},ConfigStore.Json));
            try{var installer=Path.Combine(stateDirectory,"installer-status.json");if(File.Exists(installer)&&new FileInfo(installer).Length<4096&&(File.GetAttributes(installer)&FileAttributes.ReparsePoint)==0){using var doc=JsonDocument.Parse(File.ReadAllText(installer));var r=doc.RootElement;string? Field(string name,string[] allowed)=>r.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String&&allowed.Contains(p.GetString())?p.GetString():null;Text(zip,"installer-status.json",JsonSerializer.Serialize(new{action=Field("action",["Inspect","Install","Uninstall","Repair"]),mode=Field("mode",["Auto","Deep","Manual"]),outcome=Field("outcome",["located","not-found"]),failureCode=Field("failureCode",["none","game-location-failed"])},ConfigStore.Json));}}catch(IOException){}catch(JsonException){}catch(UnauthorizedAccessException){}
            for(var i=0;i<snapshots.Length;i++){
                // Re-parse and serialize only the typed schema. Never blindly copy a file modified externally.
                var safe=new StringBuilder();
                foreach(var line in Encoding.UTF8.GetString(snapshots[i]).Split('\n')){try{var item=JsonSerializer.Deserialize<SafeLogEvent>(line,SafeOptions);if(item?.Data!=null&&item.RunId is {Length:32}&&item.RunId.All(Uri.IsHexDigit)&&Enum.IsDefined(item.Event)&&item.Component is "backend" or "game" or "hotkey" or "translation" or "sending" or "export")safe.AppendLine(JsonSerializer.Serialize(item with{Data=DiagnosticLog.Clean(item.Data)},SafeOptions));}catch(JsonException){} }
                Text(zip,$"logs/runtime-{i}.jsonl",safe.ToString());
            }
            Text(zip,"logs/backup-maintenance.jsonl",BackupMaintenance.AuditSnapshot(stateDirectory));
        }
        foreach(var old in Directory.GetFiles(directory,"DeadlockTranslate-diagnostics-*.zip").OrderByDescending(File.GetLastWriteTimeUtc).Skip(3)){if((File.GetAttributes(old)&FileAttributes.ReparsePoint)==0)File.Delete(old);}
        log.Record("export",LogKind.ExportCreated);return path;
        }finally{Gate.Release();}
    }
    private static readonly JsonSerializerOptions SafeOptions=new(JsonSerializerDefaults.Web){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
}
