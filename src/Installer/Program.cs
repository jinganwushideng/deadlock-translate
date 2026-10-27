using System.Diagnostics;
using System.Security.Principal;

namespace DeadlockTranslate.Setup;
internal static class Program {
    [STAThread]static void Main(string[] args){
        ApplicationConfiguration.Initialize();
        if(args.Length==2&&args[0] is "--release-notes-preview" or "--announcement-preview"){
            try{ReleaseNotesWindow.SavePreviews(args[1],args[0]=="--announcement-preview");}catch{Environment.ExitCode=1;}return;
        }
        var ownerIndex=Array.IndexOf(args,"--owner");
        using var identity=WindowsIdentity.GetCurrent();
        if(ownerIndex>=0&&(ownerIndex+1>=args.Length||args[ownerIndex+1]!=identity.User?.Value)){MessageBox.Show("请使用原 Windows 用户确认 UAC，不能切换到另一个管理员账户。没有执行安装。","同频安装器");return;}
        ResumeState? resume=null;var resumeIndex=Array.IndexOf(args,"--resume");
        if(resumeIndex>=0){try{if(ownerIndex<0||resumeIndex+1>=args.Length)throw new IOException("恢复选择需要原用户标识。");resume=ResumeState.Load(args[resumeIndex+1],identity.User!.Value,InstallerVersion.Current);}catch(Exception e){MessageBox.Show(e.Message,"同频安装器");return;}}
        if(args.Length==2&&args[0]=="--verify-package"){
            try{using var package=new Package();package.Prepare(_=>{},CancellationToken.None).GetAwaiter().GetResult();File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new{verified=true,version=InstallerVersion.Current,files=Directory.GetFiles(package.Payload,"*",SearchOption.AllDirectories).Length,installed=false}));}
            catch(Exception e){File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new{verified=false,error=e.Message}));Environment.ExitCode=1;}return;
        }
        var shot=Array.IndexOf(args,"--preview");
        if(shot>=0&&shot+1<args.Length){using var preview=new Wizard(preview:true);preview.SavePreview(args[shot+1]);return;}
        Mutex mutex;try{mutex=new Mutex(false,"Local\\DeadlockTranslate-Setup-"+identity.User?.Value);}catch(UnauthorizedAccessException){MessageBox.Show("另一个权限级别的安装器可能仍在运行，请先关闭它后重试。","同频安装器");return;}
        using var mutexScope=mutex;
        var first=false;try{first=mutex.WaitOne(ownerIndex>=0?10000:0);}catch(AbandonedMutexException){first=true;}
        if(!first){MessageBox.Show("已有一个安装器窗口，请先完成或关闭它。","同频安装器");return;}
        var smoke=Array.IndexOf(args,"--startup-check");
        using var form=new Wizard(shot>=0||smoke>=0,resume);
        if(smoke<0){form.Shown+=(_,_)=>form.ShowFirstVersionNotes();}
        try{
            if(shot>=0&&shot+1<args.Length){form.SavePreview(args[shot+1]);return;}
            if(smoke>=0&&smoke+1<args.Length){
                form.StartPosition=FormStartPosition.Manual;form.Location=new System.Drawing.Point(-32000,-32000);form.ShowInTaskbar=false;
                form.Shown+=async(_,_)=>{try{await form.RunStartupCheck(args[smoke+1]);}catch(Exception e){Directory.CreateDirectory(args[smoke+1]);File.WriteAllText(Path.Combine(args[smoke+1],"startup-check.json"),System.Text.Json.JsonSerializer.Serialize(new{ok=false,error=e.Message}));form.Close();Environment.ExitCode=1;}};
            }
            Application.Run(form);
        }finally{mutex.ReleaseMutex();}
    }
}
