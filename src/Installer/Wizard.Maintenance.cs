using System.Diagnostics;
using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed partial class Wizard
{
    private CancellationTokenSource? operationCancellation;
    private bool closeAfterWork;
    private JsonElement health;
    private string? conflictTransaction;
    private readonly ReleaseUpdates releaseUpdates = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockTranslate"), InstallerVersion.Current);

    private void DrawMaintenance()
    {
        foreach (var pending in Items(Field(snapshot,"pending"))) {
            var section = Section("发现上次未完成的安装", $"版本 {Str(pending,"version")} · {Str(pending,"game")}");
            var steamPending = Str(pending,"phase") == "steam";
            Note(section, steamPending ? "程序和游戏文件已写入，可继续完成 Steam 启动项，或保留当前安装并手动配置。" : "安装器会核对已经写入的文件，保留在此之后被其他程序改动的文件。");
            ButtonRow(section, AsyncButton(steamPending ? "预览并重试启动项" : "继续修复…", () => ResolveInterrupted(pending, true, false)),
                AsyncButton("恢复安装前状态…", () => ResolveInterrupted(pending, false, false)));
            if (steamPending) ButtonRow(section, AsyncButton("保留安装，手动配置…", () => ResolveInterrupted(pending,false,true)));
            if(conflictTransaction==Str(pending,"id"))ButtonRow(section,AsyncButton("保留外部改动，结束恢复…",()=>ResolveInterrupted(pending,false,false,true)));
        }
        var card = Section("一键体检", "检查游戏、安装文件、证书、后台和当前 Steam 启动项。");
        ButtonRow(card, AsyncButton("开始体检", ShowHealth));
        DrawHealthReport(card);
    }

    private void DrawHealthReport(FlowLayoutPanel card)
    {
        var checks = Items(Field(health,"checks")).ToArray();
        foreach(var item in checks) {
            var status = Str(item,"status") switch { "ok"=>"正常", "repair"=>"需要修复", "verify"=>"待游戏验证", _=>"需要处理" };
            Note(card,status+" · "+Str(item,"message"));
        }
        var core = checks.Any(x=>Str(x,"action")=="repair-core");
        var steamRepair = checks.Any(x=>Str(x,"action")=="steam-retry");
        if(step!=0 || Items(Field(snapshot,"pending")).Any())return;
        if(core) ButtonRow(card, Button("预览成套修复",()=>{
            mode.SelectedIndex=1;
            if(Flag(Field(snapshot,"certificate"),"ready")){step=1;Draw();}
        }));
        if(!core) ButtonRow(card,Button(steamRepair?"选择账号并修复启动项":"配置 Steam 启动项",OpenSteamManagement));
    }

    private async Task ShowHealth()
    {
        if(runner==null || busy)return;
        await Work(async()=>{
            health=await runner.Run(Request("health"));
            if(step!=4)step=0;
            Log("体检完成，请查看检查结果。");
        });
    }

    private async Task ResolveInterrupted(JsonElement pending,bool resume,bool keepCore,bool acceptChanged=false)
    {
        var steamPending=Str(pending,"phase")=="steam";
        var action=acceptChanged?"保留外部改动与恢复备份，结束本次恢复":keepCore?"保留已安装的文件，撤销未完成的 Steam 启动项改动":resume?steamPending?"重新检查并配置 Steam 启动项":"恢复可安全回退的文件，再回到安装预览":"恢复安装前的文件状态";
        if(MessageBox.Show(action+"。\n游戏："+Str(pending,"game")+"\n程序："+Str(pending,"runtime")+"\n文件变化会单独列出。",Text,MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        JsonElement selection=default;
        await Work(async()=>{
            var q=Request("restore-transaction");q["recoveryId"]=Str(pending,"id");q["resumeTransaction"]=resume;q["keepCore"]=keepCore;q["acceptChanged"]=acceptChanged;
            var result=await runner!.Run(q);
            var issues=Items(Field(result,"issues")).ToArray();
            foreach(var issue in issues)Log("已保留待处理文件 "+Str(issue,"path"));
            conflictTransaction=null;
            if(!Flag(result,"restored")){
                if(issues.Length>0&&issues.All(x=>Str(x,"reason")=="changed"))conflictTransaction=Str(pending,"id");
                errorMessage=string.Join(Environment.NewLine,issues.Select(x=>Str(x,"path")));
                return;
            }
            selection=Field(result,"selection");
            await Inspect();
            Log(Flag(result,"preserved")?"恢复已结束，外部改动和备份已保留。":keepCore?"核心安装已保留，可在体检结果中配置启动项。":"文件状态恢复完成。");
            if(resume&&!steamPending){
                game.Text=Str(selection,"game",game.Text);runtime.Text=Str(selection,"runtime",runtime.Text);
                steam.Text=Str(selection,"steam",steam.Text);
                mode.SelectedIndex=Str(selection,"mode") switch {"uninstall"=>2,"repair"=>1,_=>0};
                configure.Checked=Flag(selection,"configureSteam");stopSteam.Checked=Flag(selection,"stopSteam");
                admin.Checked=Flag(selection,"admin");restoreConflicts.Checked=Flag(selection,"restoreConflicts");
                removeRuntime.Checked=Flag(selection,"removeRuntime");removeCertificate.Checked=Flag(selection,"removeCertificate");
                step=1;
            }
        },true);
        if(resume&&steamPending&&selection.ValueKind==JsonValueKind.Object)await RetrySteam(selection);
    }

    private async Task RetrySteam(JsonElement source)
    {
        var q=Request("steam-retry");
        foreach(var key in new[]{"runtime","steam"}){
            var value=Str(source,key);
            if(value.Length>0)q[key]=value;
        }
        var ids=Field(source,"userIds").ValueKind==JsonValueKind.Array
            ? Items(Field(source,"userIds")).Select(v=>v.GetString()!).ToArray()
            : Str(source,"userId") is {Length:>0} oldId?[oldId]:SelectedSteamIds();
        if(string.IsNullOrWhiteSpace(q["steam"]?.ToString())||ids.Length==0){
            MessageBox.Show("请先选择 Steam 目录，并至少勾选一个账号。",Text);return;
        }
        q["userIds"]=ids;
        JsonElement preview=default;
        await Work(async()=>{q["action"]="steam-review";preview=await runner!.Run(q);});
        if(preview.ValueKind!=JsonValueKind.Object||closeAfterWork)return;
        var accounts=Items(Field(preview,"accounts")).ToArray();
        var summary=string.Join(Environment.NewLine,accounts.Select(DescribeSteamAccount));
        if(Flag(preview,"blocked")){MessageBox.Show("请先处理无法配置的账号，或取消勾选后重新预览。\n\n"+summary,Text);return;}
        var eligible=accounts.Where(x=>!Flag(x,"skipped")).ToArray();
        if(eligible.All(x=>Flag(x,"alreadyConfigured"))){
            LogSkippedSteamAccounts(Field(preview,"accounts"));
            var status=eligible.Length>0?"可配置账号的启动项已匹配。":"所选账号没有 Deadlock 本地记录，已全部跳过。";
            Log(status);
            if(accounts.Any(x=>Flag(x,"skipped")))MessageBox.Show(status+"\n\n"+summary,Text);
            return;
        }
        if(ReleaseUpdates.GameRunning()){MessageBox.Show("请退出 Deadlock 后再点“预览所选账号”执行。\n\n"+summary,Text);return;}
        if(MessageBox.Show($"配置所选 Steam 账号的启动项。原文件会先备份。\n程序：{q["runtime"]}\n\n{summary}",Text,MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        q["action"]="steam-retry";q["reviewedSteamToken"]=Str(preview,"reviewToken");
        if(steamOnly)steamManagementSubmitted=true;
        await Work(async()=>{
            var result=await runner!.Run(q);LogSkippedSteamAccounts(Field(result,"accounts"));
            await Inspect();health=await runner.Run(Request("health"));Log("可配置账号的 Steam 启动项已检查。");
        },true);
    }

    private void SaveInstallerSource()
    {
        var exe=Environment.ProcessPath;
        if(exe==null||!Path.GetFileName(exe).StartsWith("DeadlockTranslate-Setup",StringComparison.OrdinalIgnoreCase))return;
        var path=MaintenanceInspection.SetupRecord(state);
        Package.NoLinks(path);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(temp,JsonSerializer.Serialize(new SetupSource(exe,Package.Hash(exe),BuildInfo.Id),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temp,path,true);
        } finally { File.Delete(temp); }
    }

    private void ShowUpdates()
    {
        using var dialog=new Form{Text="同频 · 检查更新",StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(540,320),BackColor=bg,ForeColor=ink,Font=Font};
        using var lifetime=new CancellationTokenSource();
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(16)};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label=new Label{AutoSize=true,MaximumSize=new Size(500,0)};
        var notes=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=card,ForeColor=ink};
        var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
        var check=Button("检查更新",()=>{});var download=Button("下载安装包",()=>{});var cancel=Button("取消下载",releaseUpdates.CancelDownload);var start=Button("打开安装器",()=>{});
        buttons.Controls.AddRange([check,download,cancel,start]);layout.Controls.Add(label);layout.Controls.Add(notes);layout.Controls.Add(buttons);dialog.Controls.Add(layout);
        string? error=null;bool checking=false;
        void Render(){
            var result=releaseUpdates.Status();
            label.Text=$"当前 {result.CurrentVersion}"+(result.Version==null?" · 暂无可用新版":$" · 最新 {result.Version} · {result.Size/1048576.0:F1} MB")+
                (checking?" · 正在检查":result.Download=="downloading"?$" · {result.Downloaded/1048576.0:F1} MB 已下载":"");
            var description=string.Join(Environment.NewLine,new[]{error,result.CheckError,result.DownloadError,result.Summary}.Where(x=>!string.IsNullOrWhiteSpace(x)));
            if(notes.Text!=description)notes.Text=description;
            check.Enabled=!checking;download.Enabled=result.Version!=null&&result.Download!="downloading";cancel.Enabled=result.Download=="downloading";start.Enabled=result.Download=="ready";
        }
        async Task Act(Func<Task> action){
            try{error=null;await action();}
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception ex){error=ex.Message;}
            if(!dialog.IsDisposed)Render();
        }
        check.Click+=async(_,_)=>{checking=true;Render();await Act(async()=>{await releaseUpdates.Check(false,lifetime.Token);});checking=false;if(!dialog.IsDisposed)Render();};
        download.Click+=async(_,_)=>await Act(()=>{releaseUpdates.StartDownload();return Task.CompletedTask;});
        start.Click+=async(_,_)=>await Act(async()=>{
            if(MessageBox.Show(dialog,"退出 Deadlock 后打开已校验的安装器？",Text,MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
            await releaseUpdates.StartInstaller(lifetime.Token,waitPrevious:true);dialog.Close();
            BeginInvoke(Close);
        });
        using var ticker=new System.Windows.Forms.Timer{Interval=500};ticker.Tick+=(_,_)=>{try{Render();}catch(Exception ex){label.Text=ex.Message;}};
        dialog.Shown+=(_,_)=>ticker.Start();dialog.FormClosed+=(_,_)=>{ticker.Stop();lifetime.Cancel();};Render();dialog.ShowDialog(this);
    }
}
