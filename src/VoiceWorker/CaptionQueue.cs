namespace DeadlockVoiceWorker;

internal sealed record CaptionWork(long Epoch, long Part, float[] Samples, long At, int Utterance, int DurationMs,
    bool Preview = false, bool Recovery = false, int BudgetMs = 0);

// Used under the capture lock. Keep the newest audio during bursts and feed
// contiguous queued pieces of the same open-mic interval to one inference.
internal sealed class CaptionQueue
{
    private const int Capacity = 2;
    private const int MaximumMergedSamples = 80_000;
    private readonly List<CaptionWork> pending = new(Capacity);

    public bool IsEmpty => pending.Count == 0;

    public CaptionWork? Enqueue(CaptionWork work, out bool merged)
    {
        merged = pending.Count > 0 && pending[^1] is { } previous &&
            previous.Epoch == work.Epoch && previous.Utterance == work.Utterance &&
            previous.Samples.Length + work.Samples.Length <= MaximumMergedSamples &&
            work.At - previous.At <= work.DurationMs + 400;
        if (merged)
        {
            var tail = pending[^1];
            var joined = new float[tail.Samples.Length + work.Samples.Length];
            tail.Samples.CopyTo(joined, 0);
            work.Samples.CopyTo(joined, tail.Samples.Length);
            pending[^1] = work with { Samples = joined, DurationMs = tail.DurationMs + work.DurationMs };
            return null;
        }

        var dropped = pending.Count == Capacity ? Take() : null;
        pending.Add(work);
        return dropped;
    }

    public CaptionWork? Take(bool newestFirst = false)
    {
        if (IsEmpty) return null;
        int index = newestFirst && pending.Count == 2 && pending[0].Utterance < pending[1].Utterance ? 1 : 0;
        var work = pending[index];
        pending.RemoveAt(index);
        return work;
    }

    public void Clear() => pending.Clear();
}
