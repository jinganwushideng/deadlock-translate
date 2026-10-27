using System.Security.Principal;
using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed record ResumeState(string Owner, string Version, DateTimeOffset Created, string Mode, string Game, string Runtime, string Steam, string UserId, bool ConfigureSteam, bool StopSteam, bool Admin, bool RestoreConflicts, bool RemoveRuntime, bool RemoveCertificate)
{
    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockTranslate-Installer");
    static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复选择的路径包含链接。");
    }
    public string Save()
    {
        NoLinks(Root); Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "resume-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(this)); return path;
    }
    public static ResumeState Load(string path, string owner, string version)
    {
        path = Path.GetFullPath(path); NoLinks(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("resume-") || !Guid.TryParseExact(name[7..], "N", out _) || Path.GetExtension(path) != ".json") throw new IOException("恢复选择文件不属于安装器目录。");
        if (new FileInfo(path).Length > 8192) throw new IOException("恢复选择文件过大。");
        var value = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path)) ?? throw new IOException("恢复选择文件无效。");
        if (value.Owner != owner || value.Version != version || value.Created > DateTimeOffset.UtcNow.AddMinutes(1) || value.Created < DateTimeOffset.UtcNow.AddMinutes(-15) || value.Mode is not ("install" or "repair" or "uninstall")) throw new IOException("恢复选择已过期、版本不匹配或 Windows 用户不一致。");
        foreach (var p in new[] { value.Game, value.Runtime, value.Steam })
            if (p == null || p.Length > 512 || p.Contains('\0')) throw new IOException("恢复选择包含无效目录。");
        File.Delete(path); return value;
    }
}
