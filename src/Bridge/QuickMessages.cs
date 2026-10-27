namespace DeadlockTranslate;

public sealed record QuickMessage(string Id, string Text, string Hotkey, string Channel = "current", bool Enabled = true);
public sealed record QuickMessagePress(string Session, long At, QuickMessage Message, string Channel);
public static class QuickMessages {
    public static void Validate(Settings settings) {
        var items = settings.QuickMessages;
        if (items == null || items.Length > 10 || items.Select(q => q?.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("快捷短语最多 10 条，编号不能重复");
        var chords = new List<HotkeyChord> {HotkeyChord.Parse(settings.OutgoingHotkey)};
        foreach (var q in items) {
            if (q == null || string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 40 || q.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 1024 || q.Channel is not ("current" or "team" or "all" or "party"))
                throw new ArgumentException("快捷短语需要有效内容（最多 1024 字符）、编号和频道");
            var chord = HotkeyChord.Parse(q.Hotkey);
            if (!q.Enabled) continue;
            if (chords.Any(c => Overlap(c, chord))) throw new ArgumentException("快捷短语之间或与翻译发送快捷键冲突（包含左右修饰键的重叠），请更换组合");
            chords.Add(chord);
            OutgoingParts.Build("", q.Text, "translation", false, settings.OutgoingSplitLong);
        }
    }
    public static bool Overlap(HotkeyChord a, HotkeyChord b) => a.VirtualKey == b.VirtualKey &&
        Enumerable.Range(0, 64).Any(mask => a.Matches((KeyModifiers)mask) && b.Matches((KeyModifiers)mask));
}
