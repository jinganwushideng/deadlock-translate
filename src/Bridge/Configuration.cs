using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

public record Settings {
    public string Mode { get; init; } = "hybrid";
    public string FastProvider { get; init; } = "bing";
    public string AzureRegion { get; init; } = "";
    public string LlmBaseUrl { get; init; } = "https://api.deepseek.com/v1";
    public string Model { get; init; } = "deepseek-chat";
    public int FastTimeoutSeconds { get; init; } = 5;
    public int LlmTimeoutSeconds { get; init; } = 30;
    public int LlmConcurrency { get; init; } = 4;
    public int FastConcurrency { get; init; } = 4;
    public int OutgoingConcurrency { get; init; } = 3;
    public int OutgoingQueueLimit { get; init; } = 12;
    public int FontSize { get; init; } = 17;
    public bool BilingualNames { get; init; } = true;
    public bool Enabled { get; init; } = true;
    // Kept readable for old configurations; it no longer decides the receive scope.
    public bool TranslateOwn { get; init; } = false;
    public string IncomingScope { get; init; } = "all_players";
    public string Transport { get; init; } = "fragment";
    public int ScanIntervalMs { get; init; } = 200;
    public bool OutgoingEnabled { get; init; } = true;
    public bool ReserveSendSlot { get; init; } = true;
    public string OutgoingMode { get; init; } = "auto";
    public int OutgoingTimeoutSeconds { get; init; } = 30;
    public bool OutgoingFallbackFast { get; init; } = false;
    public string OutgoingDisplayMode { get; init; } = "translation";
    public string? AzureKey { get; init; }
    public string? LlmKey { get; init; }
}

public sealed class ConfigStore {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    public string DirectoryPath { get; }
    private Settings settings = new();
    private Dictionary<string, string> secrets = new();
    public event Action? Changed;
    public ConfigStore(string directory) {
        DirectoryPath = directory; Directory.CreateDirectory(directory);
        var secretsPath = Path.Combine(directory, "secrets.dpapi");
        if (File.Exists(secretsPath)) {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(secretsPath), null, DataProtectionScope.CurrentUser);
            secrets = JsonSerializer.Deserialize<Dictionary<string,string>>(bytes) ?? new();
        }
        var settingsPath = Path.Combine(directory, "settings.json");
        if (File.Exists(settingsPath)) {
            var raw = File.ReadAllText(settingsPath);
            settings = JsonSerializer.Deserialize<Settings>(raw, Json) ?? new();
            Validate(settings);
            // A configuration written before 0.2.2 has no receive scope. The requested default is
            // every player's visible chat, so migrate once and keep every other value untouched.
            using var document = JsonDocument.Parse(raw);
            if (!HasProperty(document.RootElement, "incomingScope")) {
                settings = settings with { IncomingScope = "all_players" };
                WriteSettings(settings);
            }
        } else Validate(settings);
    }
    public Settings Current { get { lock (gate) return settings; } }
    public string Secret(string name) { lock (gate) return secrets.GetValueOrDefault(name, ""); }
    public string LlmSecretFor(Settings snapshot){lock(gate){
        if(snapshot.LlmBaseUrl!=settings.LlmBaseUrl || snapshot.Model!=settings.Model)throw new InvalidOperationException("大模型配置已变更，旧排队任务未继续请求，请重试");
        return secrets.GetValueOrDefault("llm","");
    }}
    public object Public() => new { settings = Current with { AzureKey = null, LlmKey = null }, hasAzureKey = Secret("azure").Length > 0, hasLlmKey = Secret("llm").Length > 0 };
    public void Save(Settings value) {
        Validate(value);
        lock (gate) {
            var updated = new Dictionary<string,string>(secrets);
            if (value.AzureKey != null) updated["azure"] = value.AzureKey.Trim();
            if (value.LlmKey != null) updated["llm"] = value.LlmKey.Trim();
            var clean = value with { AzureKey = null, LlmKey = null };
            WriteAtomic(Path.Combine(DirectoryPath, "secrets.dpapi"), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(updated), null, DataProtectionScope.CurrentUser));
            WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(clean, Json));
            settings = clean; secrets = updated;
        }
        Changed?.Invoke();
    }
    private void WriteSettings(Settings value) {
        lock (gate) {
            var clean = value with { AzureKey = null, LlmKey = null };
            WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(clean, Json));
            settings = clean;
        }
    }
    private static bool HasProperty(JsonElement root, string name) {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject()) if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    public static void WriteAtomic(string path, byte[] data) { File.WriteAllBytes(path + ".tmp", data); File.Move(path + ".tmp", path, true); }
    public static void Validate(Settings s) {
        if (!new[]{"fast","quality","hybrid"}.Contains(s.Mode) || !new[]{"bing","azure"}.Contains(s.FastProvider)) throw new ArgumentException("无效的翻译模式或服务商");
        if (s.FastTimeoutSeconds is < 2 or > 15 || s.LlmTimeoutSeconds is < 5 or > 90 || s.LlmConcurrency is < 1 or > 8 || s.FastConcurrency is <1 or >8 || s.OutgoingConcurrency is <1 or >8 || s.OutgoingQueueLimit is <3 or >32) throw new ArgumentException("超时、并发数或队列容量超出范围");
        if (s.FontSize is < 12 or > 24 || s.ScanIntervalMs is < 100 or > 1000) throw new ArgumentException("显示参数超出范围");
        if (s.Transport != "fragment" && s.Transport != "navigation") throw new ArgumentException("无效的桥接模式");
        if (s.IncomingScope != "all_players" && s.IncomingScope != "teammates") throw new ArgumentException("无效的翻译范围");
        if(!new[]{"auto","fast","quality"}.Contains(s.OutgoingMode) || s.OutgoingTimeoutSeconds is <5 or >90) throw new ArgumentException("发送翻译模式或超时无效");
        if(s.OutgoingDisplayMode != "translation" && s.OutgoingDisplayMode != "bilingual") throw new ArgumentException("无效的发送内容");
        if (!Uri.TryCreate(s.LlmBaseUrl, UriKind.Absolute, out var url) || (url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback)) || url.UserInfo != "" || url.Query != "" || url.Fragment != "") throw new ArgumentException("API 地址必须为 HTTPS，或本机 HTTP 地址，且不能包含密钥、查询参数或片段");
        if (s.Model.Length is < 1 or > 150 || s.AzureRegion.Length > 80) throw new ArgumentException("模型名或区域无效");
    }
}
