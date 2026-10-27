using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeadlockTranslate.Setup;
public sealed class Package : IDisposable {
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"DeadlockTranslate-Setup",Guid.NewGuid().ToString("N"));
    public string Payload {get;private set;}="";
    public string Scripts=>Path.Combine(Root,"scripts");
    public static string Hash(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}
    public static string SafePath(string root,string relative){
        var parts=relative.Replace('\\','/').Split('/');
        if(parts.Any(p=>p.Length==0||p is "." or ".."||p.TrimEnd(' ','.')!=p||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0||System.Text.RegularExpressions.Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))throw new InvalidDataException("安装包包含不安全的文件名。");
        var path=Path.GetFullPath(Path.Combine(root,Path.Combine(parts)));
        if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包路径越界。");
        return path;
    }
    public static void NoLinks(string path){for(var p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p)){if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("目录包含链接，请选择普通本地目录。");}}
    public async Task Prepare(Action<string> status,CancellationToken ct){
        NoLinks(Path.GetTempPath());Directory.CreateDirectory(Root);NoLinks(Root);Directory.CreateDirectory(Scripts);
        var assembly=Assembly.GetExecutingAssembly();
        foreach(var name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("scripts/"))){using var s=assembly.GetManifestResourceStream(name)!;using var reader=new StreamReader(s);await File.WriteAllTextAsync(SafePath(Root,name),await reader.ReadToEndAsync(ct),new System.Text.UTF8Encoding(true),ct);}
        using var resource=assembly.GetManifestResourceStream("payload.zip")??throw new InvalidDataException("安装器未包含离线安装包，请下载完整安装器。");
        var zipPath=Path.Combine(Root,"payload.zip");await using(var file=new FileStream(zipPath,FileMode.CreateNew)){await resource.CopyToAsync(file,ct);}
        status("正在校验并准备离线安装包…");
        Payload=await Task.Run(()=>Extract(zipPath,Path.Combine(Root,"payload"),ct),ct);
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Payload,"package-manifest.json"),ct));
        if(manifest.RootElement.GetProperty("version").GetString()!=InstallerVersion.Current)throw new InvalidDataException("安装器与内置程序版本不一致。");
        if(!manifest.RootElement.TryGetProperty("buildId",out var build)||build.GetString()!=BuildInfo.Id)throw new InvalidDataException("安装器与内置程序构建不一致，请重新下载完整安装器。");
    }
    public static string Extract(string archive,string destination,CancellationToken ct){
        NoLinks(destination);Directory.CreateDirectory(destination);using var zip=ZipFile.OpenRead(archive);
        if(zip.Entries.Count is <1 or >5000 || zip.Entries.Sum(e=>e.Length)>1024L*1024*1024)throw new InvalidDataException("安装包大小或文件数量异常。");
        var volume=Path.GetPathRoot(Path.GetFullPath(destination));if(volume?.Length==3&&new DriveInfo(volume).AvailableFreeSpace<zip.Entries.Sum(e=>e.Length)+32L*1024*1024)throw new IOException("临时磁盘空间不足，请清理空间后重新打开安装器。");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in zip.Entries){ct.ThrowIfCancellationRequested();var relative=e.FullName.TrimEnd('/','\\');var path=SafePath(destination,relative);if(!seen.Add(path)||((e.ExternalAttributes>>16)&0xF000)==0xA000||e.Length>200L*1024*1024)throw new InvalidDataException("安装包含重复、链接或过大文件。");roots.Add(relative.Replace('\\','/').Split('/')[0]);}
        if(roots.Count!=1)throw new InvalidDataException("安装包必须只有一个程序根目录。");
        var buffer=new byte[65536];
        foreach(var e in zip.Entries){ct.ThrowIfCancellationRequested();var path=SafePath(destination,e.FullName.TrimEnd('/','\\'));if(e.Name.Length==0){Directory.CreateDirectory(path);continue;}Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var input=e.Open();using var output=new FileStream(path,FileMode.CreateNew);long total=0;int read;while((read=input.Read(buffer))!=0){ct.ThrowIfCancellationRequested();total+=read;if(total>e.Length)throw new InvalidDataException("安装包解压大小不符。");output.Write(buffer,0,read);}if(total!=e.Length)throw new InvalidDataException("安装包文件被截断。");}
        var root=Path.Combine(destination,roots.Single());Verify(root);return root;
    }
    public static void Verify(string root){
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"package-manifest.json")));var listed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var f in doc.RootElement.GetProperty("files").EnumerateArray()){
            var relative=f.GetProperty("file").GetString()!;var path=SafePath(root,relative);NoLinks(path);
            if(new[]{"settings.json","secrets.dpapi","install-manifest.json","credentials.json"}.Contains(Path.GetFileName(path).ToLowerInvariant())||new[]{".pfx",".dpapi",".cer"}.Contains(Path.GetExtension(path).ToLowerInvariant()))throw new InvalidDataException("安装包错误地包含个人配置或证书，已拒绝。");
            if(!listed.Add(path)||!File.Exists(path)||!Hash(path).Equals(f.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装包文件校验失败，请重新下载。");
        }
        foreach(var required in new[]{"DeadlockTranslate.exe","dlt_translate_dir.vpk","pairing.key","wwwroot/index.html","data/terms.json"})if(!listed.Contains(SafePath(root,required)))throw new InvalidDataException("安装包缺少必要文件。");
        if(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Any(p=>Path.GetFileName(p)!="package-manifest.json"&&!listed.Contains(p)))throw new InvalidDataException("安装包含未列入清单的文件。");
    }
    public void Dispose(){try{var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DeadlockTranslate-Setup"));if(Path.GetDirectoryName(Root)==parent&&Guid.TryParseExact(Path.GetFileName(Root),"N",out _)){NoLinks(Root);Directory.Delete(Root,true);}}catch{}}
}
