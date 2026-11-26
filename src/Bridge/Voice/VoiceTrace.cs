namespace DeadlockTranslate;

public sealed record VoiceTrace(string? Device = null, string? Model = null, int? Utterance = null,
    int? AudioMs = null, int? SpeechMs = null, double? LevelDb = null, int? Segments = null, string? Language = null,
    string? ExecutionBackend = null, double? CpuMs = null)
{
    internal VoiceTrace Clean() => new(
        Device is "cpu" or "vulkan" or "cuda" ? Device : null,
        Model is "custom" || VoiceModels.Presets.Any(preset => preset.Id == Model) ? Model : null,
        Utterance is >= 1 and <= int.MaxValue ? Utterance : null,
        AudioMs is >= 0 and <= 3_600_000 ? AudioMs : null,
        SpeechMs is >= 0 and <= 3_600_000 ? SpeechMs : null,
        LevelDb is >= -120 and <= 0 ? Math.Round(LevelDb.Value, 1) : null,
        Segments is >= 0 and <= 10000 ? Segments : null,
        Language is { Length: 2 or 3 } && Language.All(char.IsAsciiLetterLower) ? Language : null,
        ExecutionBackend is "cpu" or "vulkan" or "cuda" ? ExecutionBackend : null,
        CpuMs is >= 0 and <= 3_600_000 ? Math.Round(CpuMs.Value, 1) : null);
}

internal sealed record VoiceWorkerTrace(string Stage, int? Utterance = null, int? AudioMs = null, int? SpeechMs = null,
    double? LevelDb = null, int? Segments = null, double? ElapsedMs = null, double? CpuMs = null)
{
    internal bool Known => Stage is "voice-capture-start" or "voice-audio" or "voice-capture-end" or "voice-no-audio" or
        "voice-no-segment" or "voice-segment" or "voice-queue-merged" or "voice-queue-dropped" or
        "voice-preview-start" or "voice-asr-start" or "voice-asr-empty" or "voice-asr-discarded" or "voice-asr-expired" or
        "voice-asr-unclear" or "voice-retry-start" or "voice-retry-canceled" or "voice-retry-unconfirmed";
    internal VoiceTrace Evidence => new(Utterance: Utterance, AudioMs: AudioMs, SpeechMs: SpeechMs, LevelDb: LevelDb, Segments: Segments, CpuMs: CpuMs);
}
