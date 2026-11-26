using System.Text.RegularExpressions;

namespace DeadlockVoiceLab.Asr;

public static partial class HallucinationFilter
{
    private static readonly HashSet<string> KnownSilencePhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you.", "thank you!", "thanks for watching", "thanks for watching.",
        "thank you for watching", "thank you for watching.", "please subscribe", "like and subscribe",
        "subtitles by", "amara.org", "copyright", "music", "silence", "applause", "laughter",
        "the end", "thecynicalweeb", "sbs", "mbc", "ytn",
        "the", "a", "an", "of", "to", "and", "or", "in", "on", "at", "it", "is", "um", "uh", "hmm", "hm",
        "谢谢观看", "感谢观看", "请不吝点赞", "字幕由", "字幕提供"
    };

    private static readonly HashSet<string> AllowedShortCallouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ult", "rejuv", "urn", "bkb", "help", "mid", "blue", "yellow", "green", "purple",
        "push", "back", "run", "rosh", "baron", "hex", "curse", "heal", "go", "wait", "no", "yes",
        "撤", "退", "撤退", "快跑", "救命", "назад", "уходи", "помогите"
    };

    public static bool ShouldDropSegment(string? text, float probability, float noSpeechProbability, bool effectLike = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        bool hasNoSpeech = float.IsFinite(noSpeechProbability) && noSpeechProbability > 0f;
        bool hasProb = float.IsFinite(probability) && probability > 0f;

        if (hasNoSpeech && noSpeechProbability >= 0.78f) return true;
        if (hasNoSpeech && noSpeechProbability >= 0.60f && hasProb && probability < 0.45f) return true;

        var cleaned = CleanAndFilter(text, out bool alreadyFiltered);
        if (alreadyFiltered) return true;

        var normalizedWords = cleaned.TrimEnd('.', '!', '?', ',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool shortCallout = normalizedWords.Length == 1 &&
            AllowedShortCallouts.Contains(normalizedWords[0].TrimEnd('。', '！', '？', '，'));
        if (effectLike && (noSpeechProbability >= 0.50f || !hasProb || probability < 0.70f)
            && !(shortCallout && probability >= 0.45f && noSpeechProbability < 0.50f))
            return true;
        if (hasProb && probability < 0.25f && normalizedWords.Length <= 2 &&
            !shortCallout)
        {
            return true;
        }

        return false;
    }

    public static string CleanAndFilter(string rawText, out bool filteredOut)
    {
        filteredOut = false;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            filteredOut = true;
            return "";
        }

        var cleaned = BracketTagsRegex().Replace(rawText, " ");
        cleaned = WatermarkHandleRegex().Replace(cleaned, " ");
        cleaned = SnakeCaseHandleRegex().Replace(cleaned, " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim();
        // If watermark stripping left surrounding quotes around the whole utterance (e.g. 'TheCynicalWeeb "real speech"'), trim outer quotes cleanly
        if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"' && cleaned.Count(c => c == '"') == 2)
        {
            cleaned = cleaned[1..^1].Trim();
        }
        cleaned = LeadingDashRegex().Replace(cleaned, "");
        cleaned = InlineDialogueDashRegex().Replace(cleaned, " ").Trim();
        if (cleaned.Length == 0)
        {
            filteredOut = true;
            return "";
        }

        var normalized = cleaned.TrimEnd('.', '!', '?', ',', ' ');
        if (normalized.Length == 0 || KnownSilencePhrases.Contains(cleaned) || KnownSilencePhrases.Contains(normalized))
        {
            filteredOut = true;
            return "";
        }

        if (normalized.StartsWith("thanks for watching", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("thank you for watching", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("subtitles by", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("transcribed by", StringComparison.OrdinalIgnoreCase))
        {
            filteredOut = true;
            return "";
        }

        // Reject repetitive looping hallucinations (e.g. "the the the the" or "啊 啊 啊 啊")
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool repeatedLetter = normalized.Length >= 8 && char.IsLetter(normalized[0]) &&
            normalized.All(letter => letter == normalized[0]);
        if (repeatedLetter || words.Length >= 4 && words.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            filteredOut = true;
            return "";
        }

        // Reject pure single-symbol noise while preserving short game callouts
        if (normalized.Length == 1 && !char.IsDigit(normalized[0]) && !AllowedShortCallouts.Contains(normalized))
        {
            filteredOut = true;
            return "";
        }

        return cleaned;
    }

    [GeneratedRegex(@"\[[^\]]{1,40}\]|\([^)]{1,40}\)|\*[^*]{1,40}\*", RegexOptions.CultureInvariant)]
    private static partial Regex BracketTagsRegex();

    [GeneratedRegex(@"\b(?:[Tt]he[A-Z][A-Za-z0-9_]{3,}|[a-z]{2,}[A-Z][a-z]+[A-Z][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WatermarkHandleRegex();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCaseHandleRegex();

    [GeneratedRegex(@"^\s*[-–—]+\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDashRegex();

    [GeneratedRegex(@"(?<=[.!?。！？])\s+[-–—]+\s+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineDialogueDashRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();
}

