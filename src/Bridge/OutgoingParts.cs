using System.Globalization;
using System.Text;

namespace DeadlockTranslate;

// Counts only: diagnostic files must never contain the outgoing message body.
public sealed record SendLengthEvidence(int OriginalCharacters, int TranslationCharacters, int CurrentPartCharacters, int CurrentPartUtf8Bytes, int PartIndex, int PartCount, int CharacterLimit, int OmittedCharacters=0) {
    public static SendLengthEvidence? For(OutgoingView view) {
        try { return new(OutgoingParts.CharacterCount(view.Original), OutgoingParts.CharacterCount(view.English), OutgoingParts.CharacterCount(view.SendText), Encoding.UTF8.GetByteCount(view.SendText), view.PartIndex, view.PartCount, OutgoingParts.Limit, view.OmittedCharacters); }
        catch (InvalidDataException) { return null; }
    }
    public SendLengthEvidence? Clean()=> OriginalCharacters is >=0 and <=16000 && TranslationCharacters is >=0 and <=16000 && CurrentPartCharacters is >=0 and <=16000 && CurrentPartUtf8Bytes is >=0 and <=64000 && PartCount is >=1 and <=8 && PartIndex>=0 && PartIndex<PartCount && CharacterLimit is >=1 and <=1024 && OmittedCharacters is >=0 and <=16000 ? this : null;
}

public sealed record OutgoingPlan(string[] Parts, int OmittedCharacters);

public static class OutgoingParts {
    // CitadelChat calls TextEntry.SetMaxChars(80) after loading chat.xml. The
    // native entry stores UTF-32 characters: .NET/JS UTF-16 length is not its limit.
    public const int Limit = 80, MaximumParts = 8;
    public static int CharacterCount(string text) {
        var count = 0;
        for (var i = 0; i < text.Length; i++, count++) {
            if (char.IsHighSurrogate(text[i])) {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new InvalidDataException("发送内容包含无效 Unicode 字符，未发送");
                i++;
            } else if (char.IsLowSurrogate(text[i])) throw new InvalidDataException("发送内容包含无效 Unicode 字符，未发送");
        }
        return count;
    }
    public static string[] Build(string original, string translation, string mode, bool separate, bool splitLong)=>Plan(original,translation,mode,separate,splitLong).Parts;
    private static int PrefixEnd(string body, int[] boundaries) {
        var end = 0; var used = 0;
        for (var i = 0; i < boundaries.Length; i++) {
            var next = i + 1 < boundaries.Length ? boundaries[i + 1] : body.Length;
            used += CharacterCount(body[boundaries[i]..next]);
            if (used > Limit) break;
            end = next;
        }
        if (end == 0) throw new InvalidDataException("单个字符组合超过发送上限，未发送");
        return end;
    }
    public static OutgoingPlan Plan(string original, string translation, string mode, bool separate, bool splitLong) {
        var source = OutgoingService.CleanMessage(original);
        var target = OutgoingService.CleanMessage(translation);
        if (target.Length == 0) throw new InvalidDataException("发送内容为空，未发送");
        var bilingual = mode == "bilingual" && source.Length > 0 && !source.Equals(target, StringComparison.OrdinalIgnoreCase);
        var bodies = bilingual && separate ? new[] {source, target} : new[] {bilingual ? source + " | " + target : target};
        var parts = new List<string>(); var omitted = 0;
        foreach (var body in bodies) {
            var characters = CharacterCount(body);
            if (characters <= Limit) { parts.Add(body); continue; }
            if (!splitLong) {
                // Send the largest intact prefix; keep the full source/translation on the job.
                // Do not apply the word-boundary preference here: that would waste capacity.
                var prefix = body[..PrefixEnd(body,StringInfo.ParseCombiningCharacters(body))].TrimEnd();
                parts.Add(prefix); omitted += characters - CharacterCount(prefix); continue;
            }
            var rest = body;
            while (CharacterCount(rest) > Limit) {
                // Never break a surrogate, combining sequence or emoji sequence.
                var boundaries = StringInfo.ParseCombiningCharacters(rest);
                var end = PrefixEnd(rest,boundaries);
                var soft = rest.LastIndexOfAny([' ', '。', '！', '？', '.', '!', '?', ',', '，'], end - 1, end);
                if (soft >= end / 2 && boundaries.Contains(soft + 1)) end = soft + 1;
                var part = rest[..end].Trim();
                if (part.Length > 0) parts.Add(part);
                rest = rest[end..].TrimStart();
                if (parts.Count >= MaximumParts) throw new InvalidDataException("内容需要超过 8 条消息，未发送；请缩短内容");
            }
            if (rest.Length > 0) parts.Add(rest);
        }
        if (parts.Count > MaximumParts) throw new InvalidDataException("内容需要超过 8 条消息，未发送；请缩短内容");
        return new(parts.ToArray(),omitted);
    }
}
