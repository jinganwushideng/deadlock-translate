using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate.Setup;
public sealed partial class Wizard : Form {
    readonly Color bg=InstallerColors.Paper,card=InstallerColors.Card,ink=InstallerColors.Ink,accent=InstallerColors.Gold;
    Package package=new();readonly bool preview;Runner? runner;bool ready,busy,writing,completionNotes;int step;
    CancellationTokenSource? cancellation;JsonElement snapshot,plan;Dictionary<string,object?>? reviewed;
    readonly Panel content=new(){Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(24)};
    readonly Label title=new(){AutoSize=true},subtitle=new(){AutoSize=true},stage=new(){AutoSize=true};
    readonly ActionButton previous=new(){Text="上一步"},next=new(ButtonKind.Primary){Text="准备安装包"},cancelScan=new(){Text="取消扫描",Visible=false};
    readonly ProgressBar progress=new(){Dock=DockStyle.Bottom,Height=5,Style=ProgressBarStyle.Marquee,Visible=false};
    readonly ComboBox mode=new NativeComboBox(){DropDownStyle=ComboBoxStyle.DropDownList},game=new NativeComboBox(){DropDownStyle=ComboBoxStyle.DropDown},user=new NativeComboBox(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly TextBox runtime=new(),steam=new(),launch=new(){ReadOnly=true},details=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    readonly CheckBox configure=new(){Text="自动配置所选 Steam 账号的启动项",Checked=true,AutoSize=true},stopSteam=new(){Text="允许正常退出并重新打开 Steam（不会强制结束）",Checked=true,AutoSize=true},admin=new(){Text="游戏需要管理员权限：启用管理员兼容启动",AutoSize=true},related=new(){Text="保留相关聊天/HUD Mod，并确认共存需要游戏验证",AutoSize=true},restoreConflicts=new(){Text="卸载时恢复本安装器曾停用、且未被改动的冲突包",Checked=true,AutoSize=true},removeCertificate=new(){Text="卸载时移除本程序证书信任（Windows 可能要求确认）",AutoSize=true},renew=new(){Text="续期或重新生成损坏证书（会备份旧证书）",AutoSize=true};
    readonly CheckedListBox conflicts=new(){CheckOnClick=true,Height=150,IntegralHeight=false};
    readonly CheckBox repairMissing=new(){AutoSize=true,Text="同意修复缺失/损坏的程序文件；自定义词库保留原样"};
    readonly CheckBox removeRuntime=new(){AutoSize=true,Text="同时备份并移除清单匹配的程序文件（保留修改过的文件和个人设置）"};
    readonly List<string> log=new();readonly string state=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate");
    readonly string logFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate-Installer","installer.log");
    string completedRuntime="";
    readonly InstallerAudit audit=new();readonly ResumeState? resume;bool resumeApplied;
    readonly CheckBox allowReadOnly=new(){AutoSize=true,Text="允许临时解除 gameinfo.gi 的只读属性；完成或失败后恢复"};
    readonly CheckBox repairGameVpk=new(){AutoSize=true,Text="恢复游戏翻译包或修复改名关联；保留其他 Mod 文件"};
    public Wizard(bool preview=false,ResumeState? resume=null){
        this.preview=preview;
        this.resume=resume;
        Text="同频 · Deadlock 翻译安装器";
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        Font=new Font("Microsoft YaHei UI",10);BackColor=bg;ForeColor=ink;
        ClientSize=new Size(1080,750);MinimumSize=new Size(980,650);StartPosition=FormStartPosition.CenterScreen;
        var area=Screen.PrimaryScreen?.WorkingArea??new Rectangle(0,0,1120,800);
        MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));
        Size=new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));
        BuildLayout();
        mode.Items.AddRange(["安装 / 覆盖更新","修复已有安装","卸载游戏组件"]);mode.SelectedIndex=0;
        mode.SelectedIndexChanged+=(_,_)=>{if(!busy&&step==0)Draw();};
        conflicts.HorizontalScrollbar=true;
        conflicts.ItemCheck+=(_,e)=>{if(conflicts.Items[e.Index] is Conflict {Kind:"related"})e.NewValue=CheckState.Unchecked;};
        next.Click+=async(_,_)=>await Next();previous.Click+=(_,_)=>{if(!busy&&step>0){step--;Draw();}};
        cancelScan.Click+=(_,_)=>cancellation?.Cancel();
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;if(!suppressDialogs)MessageBox.Show(writing?"正在执行或等待 Windows 确认，请完成当前步骤后关闭。":"请先取消扫描或等待检查结束。",Text);}};
        FormClosed+=(_,_)=>package.Dispose();Draw();if(!preview)Shown+=async(_,_)=>await Prepare();
    }
    string Mode=>mode.SelectedIndex switch{1=>"repair",2=>"uninstall",_=>"install"};
    static string Str(JsonElement e,string n,string fallback="")=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??fallback:fallback;
    static bool Flag(JsonElement e,string n)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var p)&&p.ValueKind==JsonValueKind.True;
    static JsonElement Field(JsonElement e,string n)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(n,out var p)?p:default;
    static IEnumerable<JsonElement> Items(JsonElement e)=>e.ValueKind==JsonValueKind.Array?e.EnumerateArray():[];
    string Clean(string s)=>Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
    Dictionary<string,object?> Request(string action)=>new(){["action"]=action,["state"]=state,["game"]=Clean(game.Text),["runtime"]=Clean(runtime.Text),["steam"]=Clean(steam.Text),["userId"]=user.SelectedItem is Account a?a.Id:"",["mode"]=Mode,["admin"]=admin.Checked,["configureSteam"]=configure.Checked,["stopSteam"]=stopSteam.Checked,["allowRelated"]=related.Checked,["restoreConflicts"]=restoreConflicts.Checked,["removeCertificate"]=removeCertificate.Checked,["removeRuntime"]=removeRuntime.Checked,["allowReadOnlyGameinfo"]=allowReadOnly.Checked};
    void Log(string text){if(InvokeRequired){BeginInvoke(()=>Log(text));return;}text=InstallerAudit.Redact(text).Replace('\r',' ').Replace('\n',' ');statusLine.Text=text;log.Add(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")+"  "+text);if(log.Count>400)log.RemoveAt(0);details.Text=string.Join(Environment.NewLine,log);try{Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);if(File.Exists(logFile)&&new FileInfo(logFile).Length>1024*1024)File.Move(logFile,logFile+".1",true);File.AppendAllText(logFile,log[^1]+Environment.NewLine,Encoding.UTF8);}catch{}}
    async Task Work(Func<Task> action,bool mutating=false){if(busy)return;busy=true;writing=mutating;errorMessage="";progress.Visible=true;SetInteractive(false);next.Enabled=previous.Enabled=false;navigation.Busy=true;navigation.Invalidate();try{await action();}catch(OperationCanceledException){Log("检查已取消。");}catch(Exception e){audit.Record("ui-failure",new{type=e.GetType().Name,message=InstallerAudit.Redact(e.Message)});errorMessage=e.Message;Log(e.Message);if(!suppressDialogs)MessageBox.Show(e.Message,"同频 · 当前步骤未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}finally{busy=writing=false;progress.Visible=false;cancelScan.Visible=false;Draw();}}
    async Task Prepare()=>await Work(async()=>{ready=false;Log("准备并校验内置安装包…");package.Dispose();package=new();await package.Prepare(Log,CancellationToken.None);runner=new(package,Log,audit);if(resume!=null&&!resumeApplied)RestoreSelections(resume);await Inspect();if(resume!=null&&!resumeApplied){RestoreSelections(resume);resumeApplied=true;Log("已恢复管理员重开前的目录和选项。请重新检查并确认；不会自动安装。");}ready=true;Log("准备完成。请先确认本机证书。" );});
    async Task Inspect(){snapshot=await runner!.Run(Request("inspect"));var selectedGame=game.Text;game.Items.Clear();game.Text=selectedGame;foreach(var p in Items(Field(snapshot,"games")))game.Items.Add(p.GetString()!);if(string.IsNullOrWhiteSpace(game.Text))game.Text=Str(snapshot,"game");if(string.IsNullOrWhiteSpace(runtime.Text))runtime.Text=Str(snapshot,"runtime");if(string.IsNullOrWhiteSpace(steam.Text))steam.Text=Str(snapshot,"steam");var selected=(user.SelectedItem as Account)?.Id;user.Items.Clear();foreach(var u in Items(Field(snapshot,"users")))user.Items.Add(new Account(Str(u,"id"),Flag(u,"active"),Str(u,"name")));var users=user.Items.Cast<Account>().ToArray();user.SelectedItem=users.FirstOrDefault(a=>a.Id==selected)??users.FirstOrDefault(a=>a.Active)??(users.Length==1?users[0]:null);renew.Checked=Str(Field(snapshot,"certificate"),"state") is "expired" or "private-missing";}
    async Task Next(){
        if(!ready){await Prepare();return;}
        if(step==0){if(Mode=="install"&&!Flag(Field(snapshot,"certificate"),"ready")){MessageBox.Show("请先点击“准备 / 信任证书”，完成 Windows 确认。",Text);return;}step=1;Draw();return;}
        if(step==1){await Work(async()=>{plan=await runner!.Run(Request("plan"));if(Flag(plan,"reinstallSuggested")){mode.SelectedIndex=0;step=0;Log("没有可验证的安装记录，无需先卸载。已切换重新安装；继续即可检查并恢复缺失文件。");return;}allowReadOnly.Checked=related.Checked=false;repairMissing.Checked=Flag(plan,"needsRepair");repairGameVpk.Checked=Flag(plan,"needsGameRepair")&&!Flag(Field(plan,"gameVpk"),"blocked");game.Text=Str(plan,"game");runtime.Text=Str(plan,"runtime");launch.Text=Str(plan,"launch");if(Flag(plan,"fullRepair")&&!Flag(Field(snapshot,"certificate"),"ready")){step=0;Log("成套修复需要有效的本机证书，请在第一步准备或重新检测；已有有效证书不会重复安装。");return;}conflicts.Items.Clear();foreach(var c in Items(Field(plan,"conflicts")))conflicts.Items.Add(new Conflict(Str(c,"file"),Str(c,"hash"),Str(c,"kind"),string.Join(", ",Items(Field(c,"resources")).Select(v=>v.GetString()))),false);step=2;});return;}
        if(step==2){
            if(Flag(plan,"blocked")){MessageBox.Show("预检发现阻塞项，请按检查页提示处理后返回重新检测。尚未写入游戏文件。",Text);return;}
            if(Flag(plan,"needsReadOnlyConsent")&&!allowReadOnly.Checked){MessageBox.Show("游戏配置为只读，请确认检查页的临时解除只读选项，或自行处理后重新检测。",Text);return;}
            if(Flag(plan,"gameRunning")){MessageBox.Show("检测时游戏仍在运行，请退出游戏并重新检查。",Text);step=1;Draw();return;}
            var selected=conflicts.CheckedItems.Cast<Conflict>().ToArray();if((Mode=="install"||Flag(plan,"fullRepair"))&&conflicts.Items.Cast<Conflict>().Any(c=>c.Kind!="related"&&!selected.Contains(c))){MessageBox.Show("存在冲突或不可读资源包。请选择要停用的具体包，或返回处理；不会自动覆盖整包。",Text);return;}
            if((Mode=="install"||Flag(plan,"fullRepair"))&&conflicts.Items.Cast<Conflict>().Any(c=>c.Kind=="related")&&!related.Checked){MessageBox.Show("请确认相关聊天/HUD Mod 的共存选项，或先处理这些 Mod。",Text);return;}
            if((Mode=="install"||Flag(plan,"fullRepair"))&&Flag(plan,"needsRepair")&&!repairMissing.Checked){MessageBox.Show("请确认修复缺失程序文件，或返回选择正确的旧程序目录。",Text);return;}
            if((Mode=="install"||Flag(plan,"fullRepair"))&&Flag(plan,"needsGameRepair")&&!repairGameVpk.Checked){MessageBox.Show("请确认检查页的游戏 VPK 恢复选项。原位置已被占用时只使用新的空槽位。",Text);return;}
            if(Mode=="uninstall"&&!configure.Checked&&MessageBox.Show("需要自行删除所有相关 Steam 账号中的翻译启动前缀，否则残留前缀可能阻止游戏启动。确认采用手动清理？",Text,MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            if(selected.Length>0&&MessageBox.Show("所选资源包会整体停用，可能同时影响其皮肤或 UI。是否按当前选择继续？",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
            reviewed=Request("apply");reviewed["repairMissing"]=Flag(plan,"needsRepair")&&repairMissing.Checked;reviewed["repairGameVpk"]=Flag(plan,"needsGameRepair")&&repairGameVpk.Checked;reviewed["recoveryTarget"]=Str(Field(plan,"gameVpk"),"target");reviewed["disable"]=selected.Select(c=>new{file=c.File,hash=c.Hash}).ToArray();step=3;Draw();await Work(async()=>{var result=await runner!.Run(reviewed);completedRuntime=Str(result,"runtime");completionNotes=!Flag(result,"complete");foreach(var w in Items(Field(result,"warnings")))Log(w.GetString()!);foreach(var f in Items(Field(result,"failures")))Log(new InstallStepException(f).Message);Log("核心操作已完成。" );step=4;},true);if(step==3){step=1;Draw();}return;
        }
        if(step==4){step=0;reviewed=null;await Work(Inspect);}
    }
    void Browse(Control field){using var dialog=new FolderBrowserDialog{Description="请选择目录",UseDescriptionForTitle=true};if(Directory.Exists(field.Text))dialog.InitialDirectory=field.Text;if(dialog.ShowDialog(this)==DialogResult.OK)field.Text=dialog.SelectedPath;}
    async Task Deep(){using var dialog=new FolderBrowserDialog{Description="选择深层扫描的磁盘或目录（最多 60 秒）",UseDescriptionForTitle=true};if(dialog.ShowDialog(this)!=DialogResult.OK)return;cancellation=new();cancelScan.Visible=true;await Work(async()=>{var q=Request("deep");q["scanRoot"]=dialog.SelectedPath;var result=await runner!.Run(q,cancellation.Token);game.Items.Clear();foreach(var p in Items(Field(result,"Paths")))game.Items.Add(p.GetString()!);if(game.Items.Count==1)game.SelectedIndex=0;Log("扫描完成，找到 "+game.Items.Count+" 个候选。"+(Flag(result,"Limited")?"已达到范围/时间限额，可缩小范围或手动输入。":""));});cancellation.Dispose();cancellation=null;}
    void CopyLaunch(){if(launch.Text.Length>0){try{Clipboard.SetText(launch.Text);Log("启动项已复制。" );}catch{MessageBox.Show("剪贴板暂不可用，请选择文本手动复制。",Text);}}}
    void OpenFolder(string path){try{if(Directory.Exists(path))Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={path}});else MessageBox.Show("目录尚不存在。",Text);}catch(Exception e){MessageBox.Show(e.Message,Text);}}
    void ExportLog(){using var dialog=new SaveFileDialog{Filter="安装日志 ZIP|*.zip",FileName="DeadlockTranslate-install-log-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;try{
        using var output=new FileStream(dialog.FileName,FileMode.Create,FileAccess.Write);using var z=new ZipArchive(output,ZipArchiveMode.Create);
        void Add(string name,string text){using var writer=new StreamWriter(z.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(InstallerAudit.Redact(text));}
        Add("installer.log",string.Join(Environment.NewLine,log));Add("installer-events.json",audit.Export());
        Add("environment.json",JsonSerializer.Serialize(new{version=audit.Version,buildId=BuildInfo.Id,runId=audit.RunId,step,mode=Mode,game=Clean(game.Text),runtime=Clean(runtime.Text),steam=Clean(steam.Text),certificateReady=Flag(Field(snapshot,"certificate"),"ready"),reinstall=Field(plan,"reinstall").ValueKind==JsonValueKind.Object?Field(plan,"reinstall"):JsonSerializer.SerializeToElement(new{}),gameVpk=Field(plan,"gameVpk").ValueKind==JsonValueKind.Object?Field(plan,"gameVpk"):JsonSerializer.SerializeToElement(new{}),runtimeIntegrity=Field(plan,"runtimeIntegrity").ValueKind==JsonValueKind.Object?Field(plan,"runtimeIntegrity"):JsonSerializer.SerializeToElement(new{}),preflight=Field(plan,"preflight").ValueKind==JsonValueKind.Array?Field(plan,"preflight"):JsonSerializer.SerializeToElement(Array.Empty<object>())},new JsonSerializerOptions{WriteIndented=true}));
        Add("README.txt","记录版本、阶段、路径、文件属性、错误代码、耗时和回退信息。仅导出本次会话；不包含聊天正文、API 密钥、Steam 启动参数、配置原文或证书私钥。可能含 Windows 用户目录，请检查后分享。空白错误路径表示该步骤未提供文件诊断。\r\n");
        Log("安装诊断已导出。" );
    }catch(Exception e){MessageBox.Show(e.Message,Text);}}
    void ShowNotices(){using var window=new Form{Text="同频 · 许可与说明",Size=new Size(820,600),StartPosition=FormStartPosition.CenterParent,BackColor=bg};var text=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=card,ForeColor=ink,Font=Font};using var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("installer-notices.txt");text.Text=stream==null?"同频安装器遵循项目 GPL-3.0-only 许可。完整源码位于内置运行包的 source 目录；运行库使用其各自许可。":new StreamReader(stream).ReadToEnd();window.Controls.Add(text);window.ShowDialog(this);}
    void ShowReleaseNotes(){
        try { using var window=new ReleaseNotesWindow(); window.ShowDialog(this); }
        catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or FormatException){ MessageBox.Show("无法读取内置更新日志："+e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }
    public void ShowFirstVersionNotes(){
        if(preview)return;
        var marker=new InstallerAnnouncement(Path.GetDirectoryName(logFile)!);
        var show=true;
        string? revision=null;
        try{revision=InstallerAnnouncement.ContentRevision(ReleaseHistory.Load().Current);show=marker.ShouldShow(InstallerVersion.Current,revision);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or FormatException){audit.Record("update-announcement-marker-unavailable",new{type=e.GetType().Name});}
        if(!show)return;
        try{
            using var window=new ReleaseNotesWindow(announcement:true);window.ShowDialog(this);
            if(revision!=null)marker.MarkSeen(InstallerVersion.Current,revision);audit.Record("update-announcement",new{shown=true,version=InstallerVersion.Current});
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException){
            // A broken/missing marker or image must not prevent certificate preparation/install.
            audit.Record("update-announcement-unavailable",new{type=e.GetType().Name});
        }
    }
    void RestoreSelections(ResumeState value){
        mode.SelectedIndex=value.Mode switch{"repair"=>1,"uninstall"=>2,_=>0};game.Text=value.Game;runtime.Text=value.Runtime;steam.Text=value.Steam;
        user.SelectedItem=user.Items.Cast<Account>().FirstOrDefault(a=>a.Id==value.UserId);configure.Checked=value.ConfigureSteam;stopSteam.Checked=value.StopSteam;admin.Checked=value.Admin;
        restoreConflicts.Checked=value.RestoreConflicts;removeRuntime.Checked=value.RemoveRuntime;removeCertificate.Checked=value.RemoveCertificate;
        allowReadOnly.Checked=related.Checked=repairMissing.Checked=false;step=0;
    }
    void Elevate(){
        if(busy)return;using var identity=WindowsIdentity.GetCurrent();
        if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)){MessageBox.Show("当前已是管理员权限。",Text);return;}
        if(MessageBox.Show("将保存已选目录和选项并重新打开安装器。请使用原 Windows 用户确认 UAC；重开后重新检查，不会自动安装。",Text,MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        string? file=null;
        try{
            file=new ResumeState(identity.User!.Value,audit.Version,DateTimeOffset.UtcNow,Mode,Clean(game.Text),Clean(runtime.Text),Clean(steam.Text),(user.SelectedItem as Account)?.Id??"",configure.Checked,stopSteam.Checked,admin.Checked,restoreConflicts.Checked,removeRuntime.Checked,removeCertificate.Checked).Save();
            var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"};info.ArgumentList.Add("--owner");info.ArgumentList.Add(identity.User.Value);info.ArgumentList.Add("--resume");info.ArgumentList.Add(file);
            Process.Start(info);audit.Record("elevation",new{selectionsSaved=true});Close();
        }catch(System.ComponentModel.Win32Exception){if(file!=null)File.Delete(file);Log("管理员重开未完成或已取消，当前选择已保留。" );}
        catch(Exception e){if(file!=null&&File.Exists(file))File.Delete(file);Log("管理员重开失败："+e.Message);}
    }
    record Account(string Id,bool Active,string Name=""){public override string ToString()=>(Name.Length>0?Name+" · ":"")+Id+(Active?"（当前登录）":"（已保存账号）");}
    record Conflict(string File,string Hash,string Kind,string Resources){public override string ToString()=>File+" · "+(Kind=="exact"?"资源重叠":Kind=="related"?"相关聊天/HUD":"无法检查")+" · "+Resources;}
}
