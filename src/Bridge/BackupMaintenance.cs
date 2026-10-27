using System.Diagnostics;
using System.Text.Json;

namespace DeadlockTranslate;

// On-demand only; the updater uses the same script under the same cross-process lock.
public sealed class BackupMaintenance(string stateDirectory, string scriptPath) {
    readonly SemaphoreSlim gate = new(1, 1);
    public async Task<JsonElement> Run(bool clean, bool includeLegacy, string? token, CancellationToken ct) {
        if (clean && (token is not { Length: 64 } || !token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("请先检查备份占用，再执行清理。");
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("备份检查或清理正在进行，请稍后重试。");
        try {
            var script = Path.GetFullPath(scriptPath);
            CheckPath(script);
            if (!File.Exists(script)) throw new InvalidOperationException("缺少备份维护组件，请成套覆盖更新程序。");
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", clean ? "Clean" : "Inspect", "-StateDirectory", Path.GetFullPath(stateDirectory) }) start.ArgumentList.Add(arg);
            if (includeLegacy) start.ArgumentList.Add("-IncludeLegacy");
            if (clean) { start.ArgumentList.Add("-PlanToken"); start.ArgumentList.Add(token!); }
            using var process = Process.Start(start) ?? throw new IOException("无法启动备份维护组件。");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try {
                var outputTask = ReadBounded(process.StandardOutput, timeout.Token);
                var errorTask = ReadBounded(process.StandardError, timeout.Token);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), outputTask, errorTask);
                using var json = JsonDocument.Parse(await outputTask);
                if (process.ExitCode != 0 || json.RootElement.TryGetProperty("error", out _)) {
                    var message = json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
                    throw new InvalidOperationException(message ?? "备份维护失败，原备份已尽量保留。");
                }
                return json.RootElement.Clone();
            } catch {
                if (!process.HasExited) try { process.Kill(true); } catch { }
                throw;
            }
        } finally { gate.Release(); }
    }
    static async Task<string> ReadBounded(StreamReader reader, CancellationToken ct) {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096];
        for (;;) {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return text.ToString();
            if (text.Length + count > 1024 * 1024) throw new IOException("备份维护输出过大。");
            text.Append(buffer, 0, count);
        }
    }
    static void CheckPath(string path) {
        for (string? p = path; p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份维护路径包含链接。");
    }
    public static string AuditSnapshot(string stateDirectory) {
        var safe = new System.Text.StringBuilder();
        foreach (var name in new[] { "backup-maintenance.jsonl.1", "backup-maintenance.jsonl" }) {
            try {
                var path = Path.Combine(stateDirectory, name); CheckPath(path);
                if (!File.Exists(path)) continue;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 136192) continue;
                using var reader = new StreamReader(file);
                foreach (var line in reader.ReadToEnd().Split('\n')) {
                    try {
                        using var json = JsonDocument.Parse(line); var r = json.RootElement;
                        var action = r.GetProperty("action").GetString();
                        if (action is not ("automatic-clean" or "manual-clean" or "clean-failed")) continue;
                        var utc = r.GetProperty("utc").GetDateTime();
                        var bytes = r.GetProperty("removedBytes").GetInt64(); var count = r.GetProperty("removedCount").GetInt32(); var failures = r.GetProperty("failures").GetInt32();
                        if (bytes < 0 || count < 0 || failures < 0) continue;
                        safe.AppendLine(JsonSerializer.Serialize(new { utc, action, removedBytes = bytes, removedCount = count, failures }));
                    } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return safe.ToString();
    }
}

public sealed record BackupCleanupRequest(string PlanToken, bool IncludeLegacy = false);
