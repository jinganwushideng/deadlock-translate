namespace DeadlockVoiceWorker;

internal static class CaptionConfidence
{
    private static readonly HashSet<string> UnqualifiedOrders = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "go", "attack", "engage", "推", "上", "打"
    };

    internal static bool Trust(string text, float probability, float noSpeech, bool preview)
    {
        if (!float.IsFinite(probability) || !float.IsFinite(noSpeech) ||
            probability < (preview ? 0.55f : 0.45f) || noSpeech > (preview ? 0.45f : 0.60f))
            return false;
        // A clipped positive verb can invert an order when its negation is lost.
        return !preview || !UnqualifiedOrders.Contains(text.Trim().TrimEnd('.', '!', '?', '。', '！', '？'));
    }
}
