namespace DeadlockTranslate;

// Empty model / null thinking inherit existing settings, including migrated installations.
public static class LlmProfiles {
    public static bool HasOverrides(Settings settings) => !string.IsNullOrWhiteSpace(settings.IncomingModel) || !string.IsNullOrWhiteSpace(settings.OutgoingModel) || settings.IncomingThinking.HasValue || settings.OutgoingThinking.HasValue;
    public static Settings Effective(Settings settings, bool outgoing) => !settings.LlmIndependentProfiles ? settings : settings with {
        Model = string.IsNullOrWhiteSpace(outgoing ? settings.OutgoingModel : settings.IncomingModel)
            ? settings.Model : (outgoing ? settings.OutgoingModel : settings.IncomingModel).Trim(),
        LlmThinkingEnabled = (outgoing ? settings.OutgoingThinking : settings.IncomingThinking) ?? settings.LlmThinkingEnabled
    };
    public static void Validate(Settings settings) {
        foreach (var model in new[] {settings.IncomingModel, settings.OutgoingModel})
            if (model == null || model.Length > 150 || model.Any(char.IsControl)) throw new ArgumentException("独立模型名最多 150 字符，留空沿用通用模型；请完整填写服务商名称");
    }
}
