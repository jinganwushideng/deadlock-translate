using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate.Setup;

public sealed record ReleaseEntry(string Version, string Date, string Title, string[] Changes, string[] Notes)
{
    public override string ToString() => "v" + Version + (Version == InstallerVersion.Current ? " · 当前版本" : "") + (Date.Length > 0 ? " · " + Date : " · 源码记录");
    public string DisplayText => "v" + Version + "  /  " + Title + Environment.NewLine +
        (Date.Length > 0 ? Date + Environment.NewLine : "") + Environment.NewLine +
        string.Join(Environment.NewLine + Environment.NewLine, Changes.Select(s => "• " + s)) +
        (Notes.Length > 0 ? Environment.NewLine + Environment.NewLine + "说明" + Environment.NewLine + string.Join(Environment.NewLine, Notes.Select(s => "• " + s)) : "");
}

public sealed class ReleaseHistory
{
    public IReadOnlyList<ReleaseEntry> Entries { get; }
    public ReleaseEntry Current => Entries.Single(e => e.Version == InstallerVersion.Current);
    ReleaseHistory(ReleaseEntry[] entries) => Entries = Array.AsReadOnly(entries);
    public static ReleaseHistory Load()
    {
        using var stream = typeof(ReleaseHistory).Assembly.GetManifestResourceStream("installer-update-history.json")
            ?? throw new InvalidDataException("安装器未包含更新日志。");
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("更新日志格式不支持。");
        var entries = doc.RootElement.GetProperty("entries").Deserialize<ReleaseEntry[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("更新日志为空。");
        if (entries.Length is < 1 or > 128 || entries.Any(e => e == null || !Valid(e)) ||
            entries.Select(e => e.Version).Distinct(StringComparer.Ordinal).Count() != entries.Length ||
            entries[0].Version != InstallerVersion.Current ||
            !entries.Select(e => System.Version.Parse(e.Version)).SequenceEqual(entries.Select(e => System.Version.Parse(e.Version)).OrderDescending()))
            throw new InvalidDataException("更新日志版本或内容不完整。");
        return new(entries);
    }
    static bool Valid(ReleaseEntry entry)
    {
        bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Contains('\r') && !value.Contains('\n');
        return entry.Version != null && Regex.IsMatch(entry.Version, @"^\d+\.\d+\.\d+(?:\.\d+)?$") && System.Version.TryParse(entry.Version, out _) &&
            Text(entry.Title, 80) && entry.Date != null && (entry.Date.Length == 0 || DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) &&
            entry.Changes is { Length: >= 1 and <= 8 } && entry.Changes.All(s => Text(s, 240)) &&
            entry.Notes is { Length: <= 4 } && entry.Notes.All(s => Text(s, 240));
    }
}
