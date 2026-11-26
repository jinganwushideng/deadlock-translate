namespace DeadlockTranslate;

internal sealed record VoiceOptions(
    bool Enabled = false,
    string Model = "small-q5_1",
    string CustomPath = "",
    string Backend = "vulkan",
    string Language = "auto",
    string Translation = "none",
    bool TestMode = false,
    bool LightweightDraft = false)
{
    public VoiceOptions Validated()
    {
        if (Model != "custom" && !VoiceModels.Presets.Any(item => item.Id == Model))
            throw new ArgumentException("未知的语音模型。");
        if (Backend is not ("cpu" or "cuda" or "vulkan") || Translation is not ("none" or "fast" or "quality"))
            throw new ArgumentException("未知的语音识别或翻译选项。");
        if (Language is not ("auto" or "en" or "zh" or "ru" or "ja" or "ko" or "es" or "pt" or "fr" or "de"))
            throw new ArgumentException("未知的语音语言。");
        var path = (CustomPath ?? "").Trim().Trim('"');
        if (path.Length > 1024 || path.Length > 0 && (!Path.IsPathFullyQualified(path) ||
            path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2..].Contains(':') ||
            !Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请填写本地 .bin 模型的完整路径。");
        return this with { CustomPath = path };
    }
}

internal sealed record VoiceModelRequest(string Model, string Mirror);
internal sealed record VoiceOptionsUpdate(bool? Enabled, string? Model, string? CustomPath,
    string? Backend, string? Language, string? Translation, bool? TestMode = null, bool? LightweightDraft = null);
internal sealed record VoiceCaption(long Id, string Text, string? Translation, int Revision = 0, int Utterance = 0,
    bool Final = false, int? AgeSeconds = null, int? RemainingMs = null);
public sealed record VoiceShown(int Utterance, int Stages);
