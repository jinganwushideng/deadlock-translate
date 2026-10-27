using System.Text.Json;
using System.Security.Cryptography;

namespace DeadlockTranslate.Setup;

// Track announcement content as well as version; a same-version correction is shown once.
// Preview/startup-check paths never read or write the marker.
public sealed class InstallerAnnouncement(string directory) {
    private string FilePath=>Path.Combine(directory,"announcement-seen.json");
    private HashSet<string> Read(){
        Package.NoLinks(directory);Package.NoLinks(FilePath);
        if(!File.Exists(FilePath)||new FileInfo(FilePath).Length>16384)return new(StringComparer.Ordinal);
        try{return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath))??[]).Where(Valid).Take(64).ToHashSet(StringComparer.Ordinal);}
        catch(JsonException){return new(StringComparer.Ordinal);}
    }
    static bool Valid(string? key){
        if(key==null)return false;var parts=key.Split('|');
        return parts.Length is 1 or 2&&System.Version.TryParse(parts[0],out _)&&(parts.Length==1||parts[1].Length==64&&parts[1].All(c=>char.IsAsciiDigit(c)||c is >= 'a' and <= 'f'));
    }
    static string Key(string version,string? revision){var key=revision==null?version:version+"|"+revision;if(!Valid(key))throw new ArgumentException("Invalid announcement version or revision");return key;}
    public static string ContentRevision(ReleaseEntry entry)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{entry.Version,entry.Date,entry.Title,entry.Changes,entry.Notes}))).ToLowerInvariant();
    public bool ShouldShow(string version,string? revision=null){
        var key=Key(version,revision);var seen=Read();
        return revision==null?!seen.Any(k=>k.Split('|')[0]==version):!seen.Contains(key);
    }
    public void MarkSeen(string version,string? revision=null){
        var key=Key(version,revision);var seen=Read();seen.Add(key);
        var values=seen.OrderByDescending(k=>System.Version.Parse(k.Split('|')[0])).ThenBy(k=>k,StringComparer.Ordinal).Take(64).ToArray();
        Directory.CreateDirectory(directory);Package.NoLinks(directory);
        var temporary=Path.Combine(directory,".announcement-"+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllBytes(temporary,JsonSerializer.SerializeToUtf8Bytes(values));Package.NoLinks(FilePath);File.Move(temporary,FilePath,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
