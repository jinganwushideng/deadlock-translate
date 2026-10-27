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
    public int LlmMaxTokens { get; init; } = 4096;
    public bool LlmThinkingEnabled { get; init; } = true;
    public string LlmThinkingApi { get; init; } = "auto";
    public bool LlmStreaming { get; init; } = true;
    public bool LlmKeepAliveEnabled { get; init; } = false;
    public int LlmKeepAliveSeconds { get; init; } = 120;
    public bool LlmIndependentProfiles { get; init; } = false;
    public string IncomingModel { get; init; } = "";
    public string OutgoingModel { get; init; } = "";
    public bool? IncomingThinking { get; init; }
    public bool? OutgoingThinking { get; init; }
    public bool OutgoingUseContext { get; init; } = false;
    public int OutgoingContextCount { get; init; } = 3;
    public int OutgoingContextSeconds { get; init; } = 30;
    public int FastTimeoutSeconds { get; init; } = 5;
    public int LlmTimeoutSeconds { get; init; } = 30;
    public int LlmConcurrency { get; init; } = 4;
    public int FastConcurrency { get; init; } = 4;
    public int OutgoingConcurrency { get; init; } = 3;
    public int OutgoingQueueLimit { get; init; } = 12;
    public string IncomingDisplayMode { get; init; } = "native";
    public bool IncomingShowOwnTranslations { get; init; } = true;
    public string IncomingTone { get; init; } = "none";
    public string IncomingCustomTone { get; init; } = "";
    public int IncomingFeedSeconds { get; init; } = 8;
    public int IncomingFeedLines { get; init; } = 3;
    public bool IncomingShowHeroName { get; init; } = true;
    public bool IncomingShowAccountName { get; init; } = true;
    public string IncomingHeroNamePosition { get; init; } = "before";
    public int IncomingOpacity { get; init; } = 90;
    public int IncomingFeedWidth { get; init; } = 340;
    public string IncomingFeedPosition { get; init; } = "center-left";
    public int IncomingFeedOffsetX { get; init; } = 28;
    public int IncomingFeedOffsetY { get; init; } = 0;
    public string SettingsBackgroundMode { get; init; } = "static";
    public int FontSize { get; init; } = 17;
    public bool BilingualNames { get; init; } = true;
    public string ChineseNameStyle { get; init; } = "current";
    public bool Enabled { get; init; } = true;
    // Kept readable for old configurations; it no longer decides the receive scope.
    public bool TranslateOwn { get; init; } = false;
    public string IncomingScope { get; init; } = "all_players";
    public string IncomingTargetLanguage { get; init; } = "zh-Hans";
    public string IncomingCustomLanguage { get; init; } = "";
    public string IncomingCustomLanguageCode { get; init; } = "";
    public string Transport { get; init; } = "fragment";
    public int ScanIntervalMs { get; init; } = 200;
    public bool OutgoingEnabled { get; init; } = true;
    public string OutgoingHotkey { get; init; } = HotkeyChord.Default;
    public bool ReserveSendSlot { get; init; } = true;
    public string OutgoingMode { get; init; } = "auto";
    public int OutgoingTimeoutSeconds { get; init; } = 30;
    public bool OutgoingFallbackFast { get; init; } = false;
    public string OutgoingDisplayMode { get; init; } = "translation";
    public bool OutgoingSeparateBilingual { get; init; } = false;
    public bool OutgoingSplitLong { get; init; } = false;
    public QuickMessage[] QuickMessages { get; init; } = [];
    public string OutgoingTone { get; init; } = "none";
    public string OutgoingCustomTone { get; init; } = "";
    public string OutgoingEnglishCase { get; init; } = "normal";
    public string OutgoingTargetLanguage { get; init; } = "en";
    public string OutgoingCustomLanguage { get; init; } = "";
    public string OutgoingCustomLanguageCode { get; init; } = "";
    public string? AzureKey { get; init; }
    public string? LlmKey { get; init; }
}

public sealed class ConfigStore {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    public string DirectoryPath { get; }
    private Settings settings = new();
    private Dictionary<string, string> secrets = new();
    private readonly string revisionScope=Guid.NewGuid().ToString("N");
    private long revision;
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
            var migrated = false;
            if (!HasProperty(document.RootElement, "llmIndependentProfiles")) {
                settings = settings with { LlmIndependentProfiles = LlmProfiles.HasOverrides(settings) };
                // Preserve the existing file until the user saves; infer once per load.
            }
            if (!HasProperty(document.RootElement, "incomingScope")) {
                settings = settings with { IncomingScope = "all_players" };
                migrated = true;
            }
            if (migrated) WriteSettings(settings);
        } else Validate(settings);
    }
    public Settings Current { get { lock (gate) return settings; } }
    public string Secret(string name) { lock (gate) return secrets.GetValueOrDefault(name, ""); }
    public string LlmSecretFor(Settings snapshot){lock(gate){
        if(snapshot.LlmBaseUrl!=settings.LlmBaseUrl || snapshot.Model!=settings.Model)throw new InvalidOperationException("大模型配置已变更，旧排队任务未继续请求，请重试");
        return secrets.GetValueOrDefault("llm","");
    }}
    public object Public() {lock(gate)return new { settings = settings with { AzureKey = null, LlmKey = null }, hasAzureKey = secrets.GetValueOrDefault("azure","").Length > 0, hasLlmKey = secrets.GetValueOrDefault("llm","").Length > 0, revision=revisionScope+"-"+revision };}
    public bool Save(Settings value,string? expectedRevision=null) {
        Validate(value);
        value=value with{OutgoingHotkey=HotkeyChord.Parse(value.OutgoingHotkey).Canonical,QuickMessages=value.QuickMessages.Select(q=>q with{Hotkey=HotkeyChord.Parse(q.Hotkey).Canonical}).ToArray()};
        lock (gate) {
            if(expectedRevision!=null&&expectedRevision!=revisionScope+"-"+revision)throw new ConfigConflictException();
            var updated = new Dictionary<string,string>(secrets);
            if (value.AzureKey != null) updated["azure"] = value.AzureKey.Trim();
            if (value.LlmKey != null) updated["llm"] = value.LlmKey.Trim();
            var clean = value with { AzureKey = null, LlmKey = null };
            var secretChanged=updated.Count!=secrets.Count||updated.Any(p=>!secrets.TryGetValue(p.Key,out var old)||old!=p.Value);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(clean, Json);
            var settingsChanged=!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(settings, Json));
            if(!secretChanged&&!settingsChanged)return false;
            if(secretChanged)WriteAtomic(Path.Combine(DirectoryPath, "secrets.dpapi"), ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(updated), null, DataProtectionScope.CurrentUser));
            if(settingsChanged)WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), bytes);
            settings = clean; secrets = updated;
            revision++;
        }
        Changed?.Invoke();
        return true;
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
        if(s.SettingsBackgroundMode is not ("static" or "translucent" or "blur"))throw new ArgumentException("设置背景模式无效");
        OutgoingStyle.Validate(s);
        IncomingStyle.Validate(s);
        LlmProfiles.Validate(s);
        QuickMessages.Validate(s);
        DisplayPreviewOptions.From(s).Validate();
        if(!LlmRequestOptions.ValidBudget(s.LlmMaxTokens))throw new ArgumentException("模型输出上限须在 512 到 16384 token 之间");
        if(!LlmRequestOptions.ValidThinkingApi(s.LlmThinkingApi))throw new ArgumentException("无效的思考接口格式");
        if(s.LlmKeepAliveSeconds is <60 or >1800)throw new ArgumentException("大模型保活间隔须为 60～1800 秒");
        if(s.OutgoingContextCount is <1 or >3 || s.OutgoingContextSeconds is <15 or >30)throw new ArgumentException("发送上下文须为 1～3 条、15～30 秒");
        HotkeyChord.Parse(s.OutgoingHotkey);
        foreach(var name in new[]{s.IncomingCustomLanguage,s.OutgoingCustomLanguage})
            if(name==null||name.Length>80||name.Any(char.IsControl))throw new ArgumentException("自定义语言名称格式无效");
        foreach(var code in new[]{s.IncomingCustomLanguageCode,s.OutgoingCustomLanguageCode})
            if(code==null||code.Length>32||code.Any(char.IsControl))throw new ArgumentException("自定义语言代码格式无效");
        TranslationLanguages.Incoming(s);TranslationLanguages.Outgoing(s);
        if (!new[]{"fast","quality","hybrid"}.Contains(s.Mode) || !new[]{"bing","azure"}.Contains(s.FastProvider)) throw new ArgumentException("无效的翻译模式或服务商");
        if (s.FastTimeoutSeconds is < 2 or > 15 || s.LlmTimeoutSeconds is < 5 or > 90 || s.LlmConcurrency is < 1 or > 8 || s.FastConcurrency is <1 or >8 || s.OutgoingConcurrency is <1 or >8 || s.OutgoingQueueLimit is <3 or >32) throw new ArgumentException("超时、并发数或队列容量超出范围");
        if (s.FontSize is < 12 or > 24 || s.ScanIntervalMs is < 100 or > 1000) throw new ArgumentException("显示参数超出范围");
        if (s.ChineseNameStyle != "current" && s.ChineseNameStyle != "legacy") throw new ArgumentException("中文译名样式无效");
        if (s.Transport != "fragment" && s.Transport != "navigation") throw new ArgumentException("无效的桥接模式");
        if (s.IncomingScope != "all_players" && s.IncomingScope != "teammates") throw new ArgumentException("无效的翻译范围");
        if(!new[]{"auto","fast","quality"}.Contains(s.OutgoingMode) || s.OutgoingTimeoutSeconds is <5 or >90) throw new ArgumentException("发送翻译模式或超时无效");
        if(s.OutgoingDisplayMode != "translation" && s.OutgoingDisplayMode != "bilingual") throw new ArgumentException("无效的发送内容");
        if (!Uri.TryCreate(s.LlmBaseUrl, UriKind.Absolute, out var url) || (url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback)) || url.UserInfo != "" || url.Query != "" || url.Fragment != "") throw new ArgumentException("API 地址必须为 HTTPS，或本机 HTTP 地址，且不能包含密钥、查询参数或片段");
        if (s.Model.Length is < 1 or > 150 || s.AzureRegion.Length > 80) throw new ArgumentException("模型名或区域无效");
    }
}
public sealed class ConfigConflictException():Exception("另一个设置窗口已修改配置，请先处理冲突，当前输入已保留。");
