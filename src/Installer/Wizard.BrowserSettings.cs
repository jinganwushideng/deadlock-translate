using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed partial class Wizard
{
    private bool openingBrowserSettings;

    private async Task OpenBrowserSettings()
    {
        if(!ready||busy||openingBrowserSettings)return;
        openingBrowserSettings=true;
        browserSettings.Enabled=false;
        try{
            await InstalledSettingsLauncher.OpenAsync(state,info=>{
                if(IsDisposed||Disposing||closeAfterWork)return;
                if(busy||writing)throw new IOException("请等当前安装操作完成，再打开后台设置。");
                using var process=Process.Start(info)??throw new IOException("无法启动翻译后台。");
                Log("已请求打开后台和默认浏览器设置页。");
            });
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception){
            if(!IsDisposed&&!Disposing){
                Log(e.Message);
                if(!suppressDialogs)MessageBox.Show(e.Message,"同频 · 浏览器后台设置",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }finally{
            openingBrowserSettings=false;
            if(!IsDisposed&&!Disposing)browserSettings.Enabled=ready&&!busy&&!writing;
        }
    }
}

internal static class InstalledSettingsLauncher
{
    internal static async Task OpenAsync(string state,Action<ProcessStartInfo> start)
    {
        var recordPath=Path.Combine(state,"install-manifest.json");
        Package.NoLinks(recordPath);
        if(!File.Exists(recordPath))throw new IOException("还没有安装翻译后台。完成安装后即可使用此入口。");
        var guardPath=Path.Combine(state,"installation-operation.lock");
        Package.NoLinks(guardPath);
        FileStream guard;
        try{guard=new FileStream(guardPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException e){throw new IOException("安装、修复或清理仍在进行，请完成后再打开后台设置。",e);}
        using(guard){
            using var record=ReadManifest(recordPath);
            var runtime=Text(record.RootElement,"runtimeFolder");
            if(!Path.IsPathFullyQualified(runtime))throw new IOException("安装记录里的程序目录无效，请先修复安装。");
            runtime=Path.GetFullPath(runtime);
            var executable=Package.SafePath(runtime,"DeadlockTranslate.exe");
            Package.NoLinks(executable);
            var manifestPath=Package.SafePath(runtime,"package-manifest.json");
            Package.NoLinks(manifestPath);
            if(!File.Exists(executable)||!File.Exists(manifestPath))throw new IOException("已安装的后台文件不完整，请先修复安装。");
            using var package=ReadManifest(manifestPath);
            if(Text(record.RootElement,"version")!=Text(package.RootElement,"version")||
                !package.RootElement.TryGetProperty("files",out var files)||files.ValueKind!=JsonValueKind.Array)
                throw new IOException("程序包与安装记录不一致，请先修复安装。");
            var entries=files.EnumerateArray().Where(item=>Text(item,"file")=="DeadlockTranslate.exe").ToArray();
            var expected=entries.Length==1?Text(entries[0],"sha256"):"";
            if(expected.Length!=64||!expected.All(Uri.IsHexDigit))throw new IOException("后台文件缺少有效校验记录，请先修复安装。");
            await using var file=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
            var actual=Convert.ToHexString(await SHA256.HashDataAsync(file));
            if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("已安装的后台文件校验失败，请先修复安装。");
            var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=runtime};
            info.ArgumentList.Add("--settings");
            start(info);
        }
    }

    private static string Text(JsonElement value,string key)=>
        value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var property)&&property.ValueKind==JsonValueKind.String
            ?property.GetString()??"":"";

    private static JsonDocument ReadManifest(string path)
    {
        if(new FileInfo(path).Length>2*1024*1024)throw new IOException("安装记录或程序清单异常，请先修复安装。");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
