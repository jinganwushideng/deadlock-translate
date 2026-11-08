using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

internal sealed record SetupSource(string Path, string Sha256, string BuildId);
internal sealed record SteamAccountSelection(string[]? AccountIds);

internal sealed class MaintenanceInspection(string state, string runtime, int port)
{
    private readonly SemaphoreSlim gate = new(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string SetupRecord(string state) => Path.Combine(state, "installer-source.json");

    private static void NoLinks(string path)
    {
        for (var full = Path.GetFullPath(path); full is not null; full = Path.GetDirectoryName(full))
            if ((File.Exists(full) || Directory.Exists(full)) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("维护路径包含链接。");
    }

    public Task<JsonElement> Inspect(CancellationToken ct) => InspectAction("health",null,ct);
    public Task<JsonElement> SteamAccounts(CancellationToken ct) => InspectAction("steam-accounts",null,ct);
    public Task<JsonElement> SteamPreview(string[] ids,CancellationToken ct) => InspectAction("steam-review",ids,ct);
    public static string[] ValidateSteamSelection(string[]? ids)
    {
        if(ids==null||ids.Length is <1 or >32||ids.Any(id=>!uint.TryParse(id,out var value)||value==0||value.ToString()!=id))
            throw new IOException("请勾选有效的 Steam 账号，一次最多 32 个。");
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<JsonElement> InspectAction(string action,string[]? ids,CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new IOException("体检正在进行，请稍候。");
        var temp = Path.Combine(Path.GetTempPath(), "DLT-health-" + Guid.NewGuid().ToString("N"));
        try {
            var path = Path.Combine(runtime, "Installer-Workflow.ps1");
            NoLinks(path);
            if (!File.Exists(path)) throw new IOException("缺少体检组件，请使用完整安装器修复。");
            Directory.CreateDirectory(temp);
            var request = Path.Combine(temp, "request.json");var result = Path.Combine(temp, "result.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { action, state, payload = runtime, runtime=Path.TrimEndingDirectorySeparator(runtime),
                mode = "repair", port, backendOwned = true, userIds=ids }, Json), new UTF8Encoding(true), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var info = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-File", path, "-RequestFile", request, "-ResultFile", result })
                info.ArgumentList.Add(arg);
            foreach (var name in new[] { "DLT_INSTALL_JOURNAL", "DLT_INSTALL_CHILD", "DLT_INSTALL_CANCEL", "DLT_INSTALL_PROGRESS" }) info.Environment.Remove(name);
            using var process = Process.Start(info) ?? throw new IOException("无法启动体检组件。");
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new IOException("体检超时，可以重新检查或打开安装器。");
            }
            if (!File.Exists(result) || new FileInfo(result).Length > 1024 * 1024) throw new IOException("体检未返回结果，请打开安装器检查。");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result, ct));
            if (!document.RootElement.GetProperty("ok").GetBoolean()) throw new IOException("无法读取完整安装状态，请打开安装器检查。");
            return document.RootElement.GetProperty("data").Clone();
        } finally {
            foreach (var name in new[] { "request.json", "result.json", "operations.jsonl" }) {
                try { File.Delete(Path.Combine(temp, name)); } catch (IOException) { }
            }
            try { Directory.Delete(temp); } catch (IOException) { }
            gate.Release();
        }
    }

    public async Task OpenInstaller(CancellationToken ct,string[]? steamIds=null)
    {
        var record = SetupRecord(state);
        NoLinks(record);
        if (!File.Exists(record) || new FileInfo(record).Length > 8192) throw new IOException("未找到可验证的本地安装器。请打开原安装包的「一键体检」，或从项目下载完整安装器。");
        var setup = JsonSerializer.Deserialize<SetupSource>(await File.ReadAllTextAsync(record, ct), Json);
        if (setup == null || setup.BuildId != BuildInfo.Id || !Path.IsPathFullyQualified(setup.Path) ||
            !setup.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || setup.Sha256.Length != 64)
            throw new IOException("本地安装器记录不匹配，请打开当前版本的完整安装包。");
        NoLinks(setup.Path);
        if (!File.Exists(setup.Path)) throw new IOException("原安装包已移动，请从资源管理器打开它，或从项目下载完整安装器。");
        await using var stream = new FileStream(setup.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ReleaseUpdates.MaxInstallerBytes || !ReleaseUpdates.HashEquals(await SHA256.HashDataAsync(stream, ct), setup.Sha256))
            throw new IOException("原安装器校验失败，请重新取得完整安装包。");
        var launch=new ProcessStartInfo(setup.Path){UseShellExecute=true};
        if(steamIds==null)launch.ArgumentList.Add("--maintenance");
        else{launch.ArgumentList.Add("--steam-users");launch.ArgumentList.Add(string.Join(',',ValidateSteamSelection(steamIds)));}
        using var process=Process.Start(launch);
    }
}
