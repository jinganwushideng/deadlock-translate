namespace DeadlockTranslate;

// The bridge owns the opening timestamp and the HUD confirms what was shown.
// All timestamps use the same monotonic clock, including the HUD round trip.
internal sealed class VoiceLatency
{
    internal const int Original = 1, Translation = 2, Final = 4;
    internal const int Previous = 8, CurrentTranslation = 16;
    private const int MaxPending = 128;
    private const int DisplayDeadlineMs = 20_000;
    private readonly Dictionary<int, Sample> pending = [];

    private sealed class Sample(long opened)
    {
        internal readonly long Opened = opened;
        internal long? Ended;
        internal int Issued, Shown;
        internal bool Audible;
    }

    internal void Start(int id, long now)
    {
        if (pending.Count < MaxPending) pending[id] = new(now);
    }

    internal void End(int id, long now)
    {
        if (pending.TryGetValue(id, out var sample)) sample.Ended ??= now;
    }

    internal bool Audio(int id)
    {
        if (!pending.TryGetValue(id, out var sample) || sample.Audible) return false;
        sample.Audible = true;
        return true;
    }

    internal void Issue(VoiceCaption caption)
    {
        if (!pending.TryGetValue(caption.Utterance, out var sample)) return;
        sample.Issued |= Original;
        if (caption.AgeSeconds != null) sample.Issued |= Previous;
        if (!string.IsNullOrWhiteSpace(caption.Translation) && caption.Text != caption.Translation)
        {
            sample.Issued |= Translation;
            if (caption.AgeSeconds == null) sample.Issued |= CurrentTranslation;
        }
        if (caption.Final) sample.Issued |= Final;
    }

    internal void Confirm(VoiceShown receipt, long now, Action<string, int, long> report)
    {
        if (!pending.TryGetValue(receipt.Utterance, out var sample)) return;
        int added = receipt.Stages & sample.Issued & ~sample.Shown;
        sample.Shown |= added;
        long elapsed = now - sample.Opened;
        if ((added & Original) != 0) report("voice-hud-first-original", receipt.Utterance, elapsed);
        if ((added & Translation) != 0) report("voice-hud-first-translation", receipt.Utterance, elapsed);
        if ((added & Final) != 0) report("voice-hud-first-final", receipt.Utterance, elapsed);
        if ((added & Previous) != 0) report("voice-hud-previous", receipt.Utterance, elapsed);
        if ((added & CurrentTranslation) != 0) report("voice-hud-current-translation", receipt.Utterance, elapsed);
    }

    internal void Collect(long now, Action<string, int, long> report, bool reset = false)
    {
        foreach (var (id, sample) in pending)
        {
            if (!reset && (sample.Ended is not long ended || now - ended < DisplayDeadlineMs)) continue;
            if (sample.Audible && (sample.Shown & Original) == 0)
                report("voice-hud-missed", id, now - sample.Opened);
            pending.Remove(id);
        }
    }
}
