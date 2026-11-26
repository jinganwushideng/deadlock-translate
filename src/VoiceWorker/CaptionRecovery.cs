using DeadlockTranslate;

namespace DeadlockVoiceWorker;

// Owned by the worker's capture lock. Audio stays in memory for two open-mic intervals.
internal sealed class CaptionRecovery
{
    private const int MaxSamples = 30 * 16000;
    private const int PreRollSamples = 3200;
    private const int RetryBudgetMs = 1500;
    private const int MaxEstimatedRetryMs = 1250;
    private readonly Queue<float> preRoll = new(PreRollSamples);
    private Interval? current, previous;

    private sealed class Interval(long epoch, int id, IEnumerable<float> leading)
    {
        public long Epoch { get; } = epoch;
        public int Id { get; } = id;
        public List<float> Audio { get; } = new(leading);
        public long? Ended { get; set; }
        public bool Failed { get; set; }
        public bool HadFinal { get; set; }
        public bool Audible { get; set; }
        public bool Attempted { get; set; }
    }

    public void Begin(long epoch, int id)
    {
        previous = current;
        current = new(epoch, id, preRoll);
        preRoll.Clear();
    }

    public void Append(float[] audio)
    {
        if (current is { Ended: null } open)
        {
            if (open.Attempted) return;
            if (open.Audio.Count + audio.Length <= MaxSamples)
                open.Audio.AddRange(audio);
            else
            {
                open.Audio.Clear();
                open.Attempted = true;
            }
            return;
        }

        foreach (var sample in audio)
        {
            if (preRoll.Count == PreRollSamples) preRoll.Dequeue();
            preRoll.Enqueue(sample);
        }
    }

    public void End(long now, bool audible)
    {
        if (current is not { Ended: null } interval) return;
        interval.Ended = now;
        interval.Audible = audible;
    }

    public bool Contains(int id) => Find(id) != null;
    public bool IsInWindow(int id, long now) => Find(id) is { } turn &&
        (turn.Ended is not long end || now < end + VoiceCaptionTiming.RecoveryWindowMs);

    private Interval? Find(int id) => current?.Id == id ? current : previous?.Id == id ? previous : null;

    public void Complete(CaptionWork work, bool accepted)
    {
        if (work.Preview || work.Recovery || Find(work.Utterance) is not { } turn) return;
        turn.HadFinal = true;
        turn.Failed |= !accepted;
    }

    public CaptionWork? Take(long now, long part, double lastInferenceMs, bool speaking)
    {
        Expire(now);
        if (speaking || !double.IsFinite(lastInferenceMs)) return null;
        // Prefer the latest complete callout. Never start work that is unlikely
        // to finish within the retry budget, based on a recent pass on this GPU.
        var turn = Eligible(current) ? current : Eligible(previous) ? previous : null;
        if (turn is null) return null;
        turn.Attempted = true;
        return new(turn.Epoch, part, turn.Audio.ToArray(), now, turn.Id,
            turn.Audio.Count / 16, Recovery: true, BudgetMs: RetryBudgetMs);

        bool Eligible(Interval? candidate) => candidate is { Ended: long ended, Audible: true, Attempted: false } &&
            (candidate.Failed || !candidate.HadFinal) && candidate.Audio.Count >= 1920 &&
            lastInferenceMs * Math.Max(1, candidate.Audio.Count / 48_000d) <= MaxEstimatedRetryMs &&
            now + RetryBudgetMs < ended + VoiceCaptionTiming.RecoveryWindowMs;
    }

    public void Expire(long now)
    {
        if (current?.Ended is long end && now >= end + VoiceCaptionTiming.RecoveryWindowMs) current.Audio.Clear();
        if (previous?.Ended is long oldEnd && now >= oldEnd + VoiceCaptionTiming.RecoveryWindowMs) previous = null;
    }

    public void Clear()
    {
        preRoll.Clear();
        current = previous = null;
    }
}
