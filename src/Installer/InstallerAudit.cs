using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate.Setup;

public static class InstallerVersion {public static string Current => BuildInfo.Version;}

public sealed class InstallStepException(JsonElement failure) : IOException(Describe(failure))
{
    public JsonElement Failure { get; } = failure.Clone();
    static string Describe(JsonElement f)
    {
        string Get(string key) => f.TryGetProperty(key, out var v) ? v.ToString() : "";
        var lines = new List<string> { Get("message"), "阶段：" + Get("stage") + " · " + Get("code") };
        if (Get("path").Length > 0) lines.Add("文件：" + Get("path"));
        if (Get("remedy").Length > 0) lines.Add(Get("remedy"));
        if (f.TryGetProperty("rollback", out var rollback) && rollback.ValueKind == JsonValueKind.Array)
            lines.AddRange(rollback.EnumerateArray().Select(v => v.GetString() ?? ""));
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class InstallerAudit
{
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string Version { get; } = InstallerVersion.Current;
    readonly List<object> events = [];
    readonly object gate = new();
    readonly string file;
    public InstallerAudit(string? directory = null)
    {
        file = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockTranslate-Installer"), "installer-events.jsonl");
        using var identity=WindowsIdentity.GetCurrent();
        Record("session", new { version = Version, buildId = BuildInfo.Id, os = Environment.OSVersion.VersionString, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) });
    }
    public static string Redact(string value) => Regex.Replace(Regex.Replace(value, @"\b(?:sk-[A-Za-z0-9_+=/\-]{8,}|gh[pousr]_[A-Za-z0-9_]{12,}|github_pat_[A-Za-z0-9_]{12,})", "[REDACTED]"), @"(?i)(authorization\s*[:=]\s*(?:bearer\s+)?|[?&](?:token|key|secret)=)[^\s\""&]+", "$1[REDACTED]");
    public void Record(string kind, object data)
    {
        lock (gate)
        {
            var entry = new { at = DateTimeOffset.UtcNow, runId = RunId, version = Version, buildId = BuildInfo.Id, kind, data };
            // Only explicitly selected metadata is passed here, never request JSON or stdout/stderr.
            var json = Redact(JsonSerializer.Serialize(entry));
            events.Add(JsonSerializer.Deserialize<JsonElement>(json));
            if (events.Count > 500) events.RemoveAt(0);
            try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".1", true); File.AppendAllText(file, json + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        }
    }
    public string Export()
    {
        lock (gate) return JsonSerializer.Serialize(new { schema = 1, runId = RunId, version = Version, buildId = BuildInfo.Id, events }, new JsonSerializerOptions { WriteIndented = true });
    }
}
