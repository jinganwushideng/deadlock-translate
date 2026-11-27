using System.Text.Json;

namespace DeadlockTranslate;

internal static class LiteProfile
{
    public static string Open(string sharedDirectory)
    {
        var profile = Path.Combine(sharedDirectory, "lite");
        Directory.CreateDirectory(profile);
        var settingsPath = Path.Combine(profile, "settings.json");
        if (File.Exists(settingsPath)) return profile;

        var baseline = Constrain(new Settings { Mode = "fast", OutgoingMode = "fast" });
        var source = Path.Combine(sharedDirectory, "settings.json");
        if (File.Exists(source) && JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(source), ConfigStore.Json) is { } original)
        {
            baseline = baseline with
            {
                IncomingTargetLanguage = original.IncomingTargetLanguage,
                IncomingCustomLanguage = original.IncomingCustomLanguage,
                IncomingCustomLanguageCode = original.IncomingCustomLanguageCode,
                OutgoingTargetLanguage = original.OutgoingTargetLanguage,
                OutgoingCustomLanguage = original.OutgoingCustomLanguage,
                OutgoingCustomLanguageCode = original.OutgoingCustomLanguageCode,
                OutgoingHotkey = HotkeyChord.Parse(original.OutgoingHotkey).Canonical
            };
        }
        ConfigStore.Validate(baseline);
        var sourceTerms = Path.Combine(sharedDirectory, "terms.json");
        var destinationTerms = Path.Combine(profile, "terms.json");
        if (File.Exists(sourceTerms) && !File.Exists(destinationTerms))
        {
            var raw = File.ReadAllBytes(sourceTerms);
            Terms.Validate(JsonSerializer.Deserialize<Term[]>(raw, ConfigStore.Json) ?? []);
            ConfigStore.WriteAtomic(destinationTerms, raw);
        }
        // Publishing settings last allows an interrupted import to finish on the next start.
        ConfigStore.WriteAtomic(settingsPath, JsonSerializer.SerializeToUtf8Bytes(baseline, ConfigStore.Json));
        return profile;
    }

    public static Settings Constrain(Settings value) => value with
    {
        Mode = value.Mode == "quality" ? "quality" : "fast",
        OutgoingMode = value.OutgoingMode == "quality" ? "quality" : "fast",
        FastProvider = "bing",
        IncomingDisplayMode = "native",
        OutgoingSeparateBilingual = false,
        OutgoingSplitLong = true,
        VoiceInputEnabled = false,
        BuildTextAutoEnabled = false,
        LlmKeepAliveEnabled = false,
        DeepSeekCostPopup = false,
        QuickMessages = [],
        OutgoingLanguageBindings = []
    };
}
