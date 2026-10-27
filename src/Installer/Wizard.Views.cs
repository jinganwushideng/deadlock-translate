using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed partial class Wizard
{
    readonly StepNavigation navigation = new();
    readonly Label statusLine = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly FlowLayoutPanel toolButtons = new() { AutoSize = true, WrapContents = false };
    readonly List<SectionPanel> sections = [];
    FlowLayoutPanel? column;
    bool suppressDialogs;
    readonly ActionButton changeMode=new(ButtonKind.Quiet){Text="更改操作模式",Visible=false};
    [System.Runtime.InteropServices.DllImport("uxtheme.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int SetWindowTheme(IntPtr handle,string? app,string? id);

    string errorMessage = "";
    static readonly string[] StepNames = ["准备开始", "选择安装位置", "检查并确认", "正在执行", "操作结果"];
    static readonly string[] StepDescriptions = [
        "先确认本机证书，再选择游戏位置。已有证书可直接沿用。",
        "自动填写已发现的位置；找不到时可浏览、扫描或直接粘贴路径。",
        "检查目录、启动方式与资源冲突，确认后执行。",
        "安装过程中请保持窗口开启；Windows 弹窗需要本人确认。",
        "阅读执行结果和后续事项，再从 Steam 启动游戏。"
    ];

    void BuildLayout()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.Controls.Add(navigation, 0, 0);
        Controls.Add(outer);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(main, 1, 0);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(28, 25, 22, 16), Margin = Padding.Empty };
        stage.Font = new Font(Font.FontFamily, 9, FontStyle.Bold); stage.ForeColor = accent; stage.Margin = new Padding(0, 0, 0, 7);
        title.ForeColor = accent; title.Font = new Font(Font.FontFamily, 21, FontStyle.Bold); title.Margin = new Padding(0, 0, 0, 8);
        subtitle.ForeColor = InstallerColors.Muted; subtitle.Margin = Padding.Empty;
        changeMode.Click+=(_,_)=>{if(busy||writing)return;reviewed=null;plan=default;step=0;Draw();};
        header.Controls.AddRange([stage, title, subtitle, changeMode]); main.Controls.Add(header, 0, 0);
        content.Padding = Padding.Empty; content.Margin = Padding.Empty; content.BackColor = bg;
        main.Controls.Add(content, 0, 1);
        content.Resize += (_, _) => LayoutView();

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(24, 9, 24, 15), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLine.Font = new Font(Font.FontFamily, 8.5f); statusLine.ForeColor = InstallerColors.Muted; statusLine.Height = 24;
        statusLine.Margin = new Padding(0, 0, 0, 10); statusLine.Text = "内置完整运行包 · 后台与游戏 Mod 一起更新";
        footer.Controls.Add(statusLine, 0, 0); footer.SetColumnSpan(statusLine, 2);

        var more = Button("更多选项", () => { }, ButtonKind.Quiet);
        var menu = new ContextMenuStrip();
        menu.Items.Add("导出安装日志…", null, (_, _) => ExportLog());
        menu.Items.Add("以管理员权限重开…", null, (_, _) => Elevate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关于与许可证…", null, (_, _) => ShowNotices());
        more.Click += (_, _) => menu.Show(more, new Point(0, -menu.GetPreferredSize(Size.Empty).Height));
        toolButtons.Controls.Add(more);
        var updates = Button("更新日志", ShowReleaseNotes, ButtonKind.Quiet); updates.Name = "updateHistoryButton";
        toolButtons.Controls.Add(updates); footer.Controls.Add(toolButtons, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        previous.MinimumSize = next.MinimumSize = new Size(112, 42);
        next.Margin = Padding.Empty; actions.Controls.AddRange([cancelScan, previous, next]);
        footer.Controls.Add(actions, 1, 1); main.Controls.Add(footer, 0, 2);
        progress.ForeColor = accent; progress.Height = 4; Controls.Add(progress);
        AcceptButton = next;content.HandleCreated+=(_,_)=>SetWindowTheme(content.Handle,"DarkMode_Explorer",null);
    }

    Button Button(string text, Action action, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ActionButton(kind) { Text = text, Font = Font };
        b.Click += (_, _) => { if (!busy) action(); };
        return b;
    }
    Button AsyncButton(string text, Func<Task> action, ButtonKind kind = ButtonKind.Secondary)
    {
        var b = new ActionButton(kind) { Text = text, Font = Font };
        b.Click += async (_, _) => { if (!busy) await action(); };
        return b;
    }
    FlowLayoutPanel ButtonRow(FlowLayoutPanel parent, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 9, 0, 0), Tag = "full-width" };
        row.Controls.AddRange(controls); parent.Controls.Add(row); return row;
    }
    SectionPanel Section(string name, string? description = null)
    {
        var panel = new SectionPanel(); sections.Add(panel); column!.Controls.Add(panel);
        Label(panel, name, true);
        if (description != null) Label(panel, description);
        return panel;
    }
    void Label(FlowLayoutPanel parent, string text, bool heading = false)
    {
        var label = new Label
        {
            Text = text, AutoSize = true, ForeColor = heading ? ink : InstallerColors.Muted,
            BackColor = Color.Transparent, Font = heading ? new Font(Font.FontFamily, 11, FontStyle.Bold) : Font,
            Margin = new Padding(0, heading ? 0 : 6, 0, heading ? 10 : 5), Tag = "wrap-text"
        };
        parent.Controls.Add(label);
    }
    void Note(FlowLayoutPanel parent, string text)
    {
        Label(parent, text);
        var l = (Label)parent.Controls[^1]; l.Font = new Font(Font.FontFamily, 9); l.ForeColor = InstallerColors.Muted;
    }
    void Check(FlowLayoutPanel parent, CheckBox box)
    {
        box.ForeColor = ink; box.BackColor = InstallerColors.Card; box.Margin = new Padding(0, 9, 0, 4); box.Tag = "wrap-text";
        parent.Controls.Add(box);
    }
    void Row(FlowLayoutPanel parent, string caption, Control field, params Control[] buttons)
    {
        if (caption.Length > 0) Label(parent, caption, true);
        var row = new TableLayoutPanel { ColumnCount = 1 + buttons.Length, RowCount = 1, Height = 42, Margin = new Padding(0, 0, 0, 10), Padding = Padding.Empty, Tag = "full-width" };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Font = Font; field.ForeColor = ink; field.BackColor = InstallerColors.Card;
        field.Margin = new Padding(0, 4, buttons.Length > 0 ? 8 : 0, 4);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right; field.TabStop = true;
        if (field is TextBox box) box.BorderStyle = BorderStyle.FixedSingle;
        if (field is ComboBox combo && combo is not NativeComboBox) combo.FlatStyle = FlatStyle.Standard;
        var frame=new RoundedField(field){Dock=DockStyle.Fill,Margin=new Padding(0,0,buttons.Length>0?8:0,0)};row.Controls.Add(frame, 0, 0);
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons[i].Margin = new Padding(0, 0, i == buttons.Length - 1 ? 0 : 8, 0);
            row.Controls.Add(buttons[i], i + 1, 0);
        }
        parent.Controls.Add(row);
    }
    void SetInteractive(bool enabled)
    {
        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is System.Windows.Forms.Button or ComboBox or CheckBox or CheckedListBox || c is TextBox { ReadOnly: false }) c.Enabled = enabled;
                Walk(c);
            }
        }
        // Keep labels and cards enabled so system-disabled text cannot turn the page black.
        Walk(content); toolButtons.Enabled = !busy; changeMode.Enabled = enabled && !busy && !writing;
        if (enabled) user.Enabled = Mode != "uninstall";
    }
    void LayoutView()
    {
        if (column == null || column.IsDisposed) return;
        var width = Math.Max(340, content.ClientSize.Width - 48 - SystemInformation.VerticalScrollBarWidth);
        column.Width = width; column.MinimumSize = new Size(width, 0); column.MaximumSize = new Size(width, 32767);
        subtitle.MaximumSize = new Size(Math.Max(300, content.ClientSize.Width - 56), 0);
        foreach (var section in sections)
        {
            section.MinimumSize = new Size(width, 0); section.MaximumSize = new Size(width, 32767); section.Width = width;
            var inner = width - section.Padding.Horizontal;
            foreach (Control c in section.Controls)
            {
                if (c.Tag as string == "wrap-text") c.MaximumSize = new Size(inner, 0);
                if (c.Tag as string == "full-width") { c.Width = inner; c.MaximumSize = new Size(inner, 32767); }
            }
        }
    }
    void Draw()
    {
        content.SuspendLayout();
        foreach (var c in new Control[] { mode, game, user, runtime, steam, launch, details, configure, stopSteam, admin, related, restoreConflicts, removeCertificate, renew, conflicts, repairMissing, repairGameVpk, allowReadOnly, removeRuntime }) c.Parent?.Controls.Remove(c);
        foreach (var c in content.Controls.Cast<Control>().ToArray()) c.Dispose();
        sections.Clear(); content.AutoScrollPosition = Point.Empty;
        column = new FlowLayoutPanel { Location = new Point(24, 0), AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 10) };
        content.Controls.Add(column);
        title.Text = StepNames[step]; subtitle.Text = StepDescriptions[step]; stage.Text = $"STEP {step + 1:00} / 05";
        navigation.Step = step; navigation.Busy = busy; navigation.Invalidate();changeMode.Visible=step>0;changeMode.Enabled=!busy&&!writing;
        previous.Enabled = !busy && step > 0 && step < 3;
        next.Enabled = !busy; next.Text = !ready ? "重新检测" : step switch { 0 => "下一步", 1 => "检查并预览", 2 => Mode == "uninstall" ? "确认卸载" : "开始执行", 3 => "执行中…", _ => "返回首页" };

        if (errorMessage.Length > 0)
        {
            var error = Section("当前步骤未完成", errorMessage);
            Note(error, "可以重新检测或导出安装日志，排查后继续。");
        }
        if (step == 0) DrawCertificate();
        else if (step == 1) DrawLocations();
        else if (step == 2) DrawReview();
        else DrawResults();
        LayoutView(); SetInteractive(ready && !busy);
        content.ResumeLayout(true);
    }
    void DrawCertificate()
    {
        var operation = Section("选择操作"); Row(operation, "", mode);
        Note(operation, Flag(snapshot, "installed") ? "已检测到 " + Str(snapshot, "installedVersion") + "，更新将沿用原程序目录。" : "首次安装将使用独立程序目录。");
        var cert = Field(snapshot, "certificate");
        var valid = ready && Flag(cert, "ready");
        var card = Section("本机 HTTPS 证书", ready ? Str(cert, "message", "证书状态暂不可用，请重新检测。") : busy ? "正在校验运行包并读取本机状态…" : "本机检测尚未完成，请点击右下角“重新检测”。");
        if (valid) Note(card, "证书名称：DeadlockTranslate localhost  ·  有效期至 " + Str(cert, "expires"));
        else Note(card, "首次安装需要本人在 Windows 弹窗确认信任，请留在电脑前完成这一步。");
        var thumb = Str(cert, "thumbprint"); if (thumb.Length > 0) Note(card, "指纹：" + thumb);
        if (Mode != "uninstall")
        {
            ButtonRow(card,
                AsyncButton(valid ? "检查现有证书" : "准备并信任证书", async () => await Work(async () =>
                {
                    var q = Request("certificate"); q["renew"] = renew.Checked; await runner!.Run(q); await Inspect();
                    Log("证书检查完成。首次信任后请完整重启 Steam。");
                }, true), valid ? ButtonKind.Secondary : ButtonKind.Primary),
                AsyncButton("重新检测", async () => await Work(Inspect)));
            Check(card, renew);
        }
        else ButtonRow(card, AsyncButton("撤销本程序证书信任…", async () =>
        {
            if (MessageBox.Show("只撤销本程序当前证书的信任，Windows 可能需要确认。继续？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Work(async () => { await runner!.Run(Request("remove-certificate")); await Inspect(); Log("证书信任撤销步骤完成。"); }, true);
        }, ButtonKind.Danger));
        var note = Section("接下来");
        Label(note, Mode == "install" ? "证书就绪后点击右下角“下一步”，选择游戏与程序位置。" : Mode=="repair"?"已有有效证书直接沿用；成套修复需要证书就绪，不会重复导入有效证书。":"卸载可直接点击“下一步”，无需安装证书。");
        Note(note, "安装器使用当前 Windows 用户。目录需要写入权限时，可在“更多选项”中选择管理员重开。");
        if (Str(snapshot, "recordError").Length > 0) Label(note, Str(snapshot, "recordError"));
    }
    void DrawLocations()
    {
        var paths = Section("游戏与程序", "更新会同时处理后台、设置页和游戏 VPK。");
        Row(paths, "Deadlock 游戏目录", game, Button("浏览…", () => Browse(game)), AsyncButton("深层扫描", Deep));
        Note(paths, "可粘贴游戏根目录、citadel 文件夹或 deadlock.exe。扫描最多 60 秒，支持取消。");
        Row(paths, "翻译程序目录", runtime, Button("浏览…", () => Browse(runtime)));
        Note(paths, "更新沿用旧目录；首次安装可选择空目录。程序目录应放在游戏目录外。");
        var launchCard = Section("Steam 与启动方式");
        Row(launchCard, "Steam 目录", steam, Button("浏览…", () => Browse(steam)), AsyncButton("读取账号", async () => await Work(Inspect)));
        if (Mode != "uninstall") Row(launchCard, "Steam 账号", user);
        configure.Text = Mode == "uninstall" ? "清除此 Steam 目录所有账号中指向本程序的启动前缀" : "自动配置所选账号的 Deadlock 启动项";
        Check(launchCard, configure); Check(launchCard, stopSteam);
        if (Mode != "uninstall") Check(launchCard, admin);
        else
        {
            var cleanup = Section("卸载选项");
            Check(cleanup, restoreConflicts); Check(cleanup, removeCertificate); Check(cleanup, removeRuntime);
            Note(cleanup, "个人设置和备份保留。程序文件可选清理，修改过的文件会留下。");
        }
        var recovery = Section("安装记录恢复", "手动删除后可以直接选择安装或修复；这里也可以检测旧包，找不到时会引导重新安装。");
        ButtonRow(recovery, AsyncButton("恢复安装记录…", async () =>
        {
            if (MessageBox.Show("将检测原翻译包并尝试恢复记录；旧包已经删除时会引导重新安装，不要求手动复制旧 VPK。损坏记录会先备份。继续？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            await Work(async () => { var q = Request("recover"); q["replaceBrokenRecord"] = true; var result=await runner!.Run(q); await Inspect();if(Flag(result,"reinstallRequired")){mode.SelectedIndex=0;step=0;Log(Str(result,"message"));}else Log("安装记录已恢复。"); }, true);
        }));
    }
    void DrawReview()
    {
        var summary = Section(Mode == "install" ? Flag(plan, "installed") ? "覆盖更新" : "首次安装" : Mode == "repair" ? "修复安装" : "卸载游戏组件");
        Label(summary, Mode == "install" && Flag(plan, "installed") ? "当前 " + Str(plan, "version") + "  →  " + InstallerVersion.Current : "请核对以下位置与选项。");
        Label(summary, "游戏：" + game.Text + "\n程序：" + runtime.Text + "\n启动项：" + (configure.Checked ? "自动配置" : "手动配置") + (Mode != "uninstall" ? "\n账号：" + (user.SelectedItem?.ToString() ?? "尚未选择") : ""));
        var reinstall=Field(plan,"reinstall");
        if(reinstall.ValueKind==JsonValueKind.Object&&(Str(reinstall,"kind")!="fresh"||Str(reinstall,"recordState")=="invalid")){
            var recovery=Section("手动删除后的重新安装",Str(reinstall,"message"));
            Label(recovery,"原程序目录："+Str(reinstall,"originalRuntime")+"\n本次安装目录："+Str(reinstall,"runtime"));
            Note(recovery,"无需先卸载，也不需要复制旧 VPK 或手动修改记录。个人设置、密钥和有效证书保留；原记录缺失时，不猜测旧游戏配置的卸载归属。");
        }
        if(Flag(plan,"needsGameRepair")){
            var gameVpk=Field(plan,"gameVpk");var repair=Section("自动恢复游戏 VPK",Str(gameVpk,"message"));
            Label(repair,"原记录："+Str(gameVpk,"recordedTarget")+"\n恢复目标："+Str(gameVpk,"target"));
            foreach(var item in Items(Field(gameVpk,"matches")))Note(repair,item.GetString()??"");
            if(Mode=="install"||Flag(plan,"fullRepair"))Check(repair,repairGameVpk);else Note(repair,"没有可确认的安装记录时，请先恢复安装记录或选择首次安装。");
        }
        if ((Mode == "install" || Flag(plan,"fullRepair")) && Flag(plan, "needsRepair"))
        {
            var repair = Section("自动成套修复程序"); Check(repair, repairMissing);
            Note(repair, "确认后会恢复程序文件，自定义词库保持原样。");
            var integrity=Field(plan,"runtimeIntegrity");
            foreach(var issue in Items(Field(integrity,"issues"))){var code=Str(issue,"code");var reason=code switch{"missing-file"=>"文件缺失","hash-mismatch"=>"内容与原包清单不一致","record-runtime-mismatch"=>"运行目录 VPK 与游戏安装记录不一致","version-mismatch"=>"运行包与安装记录版本不同","invalid-pairing-format"=>"后台配对文件格式损坏","pairing-vpk-mismatch"=>"后台与 VPK 配对不一致","missing-manifest"=>"原运行包清单缺失","missing-file-hash"=>"原清单缺少该文件哈希","invalid-manifest"=>"原运行包清单损坏",_=>code};Label(repair,Str(issue,"file")+" · "+reason);}
            Note(repair,"配对检查只显示是否匹配，不显示配对凭据。后台与 VPK 将用同一份已校验的运行包成套替换。");
        }
        var readiness=Section("安装前检查", "此页不写入配置。开始执行前会再次检查，并测试目标目录的写入权限。");
        var checks=Items(Field(plan,"preflight")).ToArray();
        Note(readiness, "已检查 "+checks.Length+" 个目标；程序、游戏 VPK 与安装记录一起校验。");
        foreach(var check in checks.Where(v=>Flag(v,"blocking")||Flag(v,"requiresReadOnlyConsent")||Str(v,"code")=="file-locked")){
            Label(readiness, Str(check,"message")+"\n"+Str(check,"path"));Note(readiness,Str(check,"remedy"));
        }
        if(Flag(plan,"needsReadOnlyConsent"))Check(readiness,allowReadOnly);
        ButtonRow(readiness, Button("重新检查",()=>{step=1;Draw();}),Button("管理员重开",Elevate),Button("导出日志",ExportLog));
        ButtonRow(readiness,Button("打开游戏 Mod 目录",()=>OpenFolder(Path.Combine(game.Text,"game","citadel","addons"))),Button("打开程序目录",()=>OpenFolder(runtime.Text)));
        var resources = Section("游戏资源检查", conflicts.Items.Count == 0 ? "未发现需要处理的资源冲突。已识别的 QOL 包会自动忽略。" : "QOL 包按兼容规则自动忽略。勾选表示同意停用其他所选整包，请核对其功能。");
        if (conflicts.Items.Count > 0)
        {
            conflicts.Tag = "full-width"; conflicts.Margin = new Padding(0, 5, 0, 7); conflicts.BackColor = card; conflicts.ForeColor = ink;
            conflicts.Height = 150; resources.Controls.Add(conflicts); Check(resources, related);
        }
        if (Mode != "uninstall")
        {
            var prefix = Section("Steam 启动项"); Row(prefix, "", launch, Button("复制", CopyLaunch));
            Note(prefix, "自动配置失败时可粘贴到 Steam → Deadlock 属性 → 启动选项。普通游戏参数保留在末尾。");
        }
        if (Flag(plan, "gameRunning")) Label(summary, "检测时游戏仍在运行。请先退出，再返回上一步重新检查。");
    }
    void DrawResults()
    {
        var result = Section(step == 3 ? "执行进度" : completionNotes ? "核心操作完成，请查看后续事项" : "操作完成");
        details.Tag = "full-width"; details.Height = step == 3 ? 320 : 230; details.Margin = new Padding(0, 6, 0, 4);
        details.BackColor = card; details.ForeColor = ink; details.Font = new Font("Microsoft YaHei UI", 9);
        result.Controls.Add(details);
        if (step == 4)
        {
            if (Mode != "uninstall")
            {
                var manual = Section("手动启动项备用"); Row(manual, "", launch, Button("复制", CopyLaunch));
            }
            var follow = Section("下一步", Mode == "uninstall" ? "如采用手动清理启动项，请移除翻译前缀后再启动游戏。" : "首次信任证书后，完整退出并重启 Steam，再启动游戏测试。");
            ButtonRow(follow, Button("打开程序目录", () => OpenFolder(completedRuntime.Length > 0 ? completedRuntime : runtime.Text)), Button("打开备份", () => OpenFolder(Path.Combine(state, "backups"))));
        }
    }

    void ShowOffscreen()
    {
        StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; Show();
    }
    void CaptureImage(string path)
    {
        Application.DoEvents(); LayoutView(); PerformLayout(); Application.DoEvents();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); bitmap.Save(path);
    }
    public void SavePreview(string path)
    {
        ready = true; suppressDialogs = true;
        snapshot = JsonSerializer.SerializeToElement(new { installed = true, installedVersion = "0.6.8", certificate = new { ready = true, message = "已有有效且受信任的证书，无需重复安装。", expires = "2027-10-11", thumbprint = new string('A', 40) } });
        plan = JsonSerializer.SerializeToElement(new { installed = true, version = "0.6.8", conflicts = Array.Empty<object>() });
        runtime.Text = @"D:\Apps\DeadlockTranslate"; game.Text = @"D:\SteamLibrary\steamapps\common\Deadlock"; steam.Text = @"D:\Steam";
        launch.Text = "\"" + runtime.Text + "\\DeadlockTranslate.exe\" --steam-game %command%";
        user.Items.Add(new Account("123456789", true, "Steam 玩家")); user.SelectedIndex = 0;
        ShowOffscreen();
        string Variant(string name) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + name + ".png");
        step = 0; Draw(); CaptureImage(path);
        File.WriteAllText(Path.ChangeExtension(path,"layout.json"),JsonSerializer.Serialize(new{dpi=DeviceDpi,size=Size,mode=mode.Bounds,row=mode.Parent!.Bounds,section=mode.Parent.Parent!.Bounds,visible=mode.Visible,sidebar=navigation.Bounds,updateHistoryAvailable=toolButtons.Controls.Cast<Control>().Any(c=>c.Name=="updateHistoryButton"&&c.Visible)}));
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var hoverButton = Descendants(content).OfType<ActionButton>().First(b => b.Text == "重新检测");
        typeof(ActionButton).GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(hoverButton, [EventArgs.Empty]);
        CaptureImage(Variant("hover"));
        typeof(ActionButton).GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(hoverButton, [EventArgs.Empty]);
        var focusMessage = Message.Create(Handle, 0x127, new IntPtr(0x10002), IntPtr.Zero);
        base.WndProc(ref focusMessage); hoverButton.Focus(); CaptureImage(Variant("focus"));
        busy = true; Draw(); CaptureImage(Variant("busy")); busy = false;
        errorMessage = "检测未完成，请确认程序目录可访问后重新检测。"; Draw(); CaptureImage(Variant("error")); errorMessage = "";
        step = 1; Draw(); CaptureImage(Variant("locations"));
        step = 2; Draw(); CaptureImage(Variant("review"));
        plan=JsonSerializer.SerializeToElement(new{installed=true,version="0.6.16",needsReadOnlyConsent=true,blocked=false,preflight=new[]{new{path=@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\gameinfo.gi",code="read-only",requiresReadOnlyConsent=true,blocking=false,message="游戏配置为只读，需要明确允许临时解除只读",remedy="仅修改这一个配置文件的只读属性，操作后恢复；不会修改整个目录权限。"}}});
        Draw();CaptureImage(Variant("readonly"));
        plan=JsonSerializer.SerializeToElement(new{installed=true,version="0.6.20",needsGameRepair=true,needsRepair=true,gameVpk=new{state="occupied",recordedTarget=@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\addons\pak90_dir.vpk",target=@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\addons\pak91_dir.vpk",message="原位置内容已改变，将保留原文件，在新的空槽位恢复完整翻译包"},runtimeIntegrity=new{issues=new[]{new{file="pairing.key",code="pairing-vpk-mismatch"},new{file="DeadlockTranslate.exe",code="hash-mismatch"}}}});
        repairMissing.Checked=repairGameVpk.Checked=true;Draw();CaptureImage(Variant("recovery"));
        plan=JsonSerializer.SerializeToElement(new{installed=true,version="0.6.20",needsGameRepair=true,needsRepair=true,reinstall=new{kind="reinstall-in-place",recordState="missing",originalRuntime=runtime.Text,runtime=runtime.Text,message="安装记录已丢失，已确认原程序目录。保留个人数据，自动重建记录并成套重装，无需先卸载。"},gameVpk=new{state="missing",recordedTarget=@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\addons\pak90_dir.vpk",target=@"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\addons\pak90_dir.vpk",message="游戏翻译 VPK 已被手动删除，将从完整安装包恢复"}});
        Draw();CaptureImage(Variant("reinstall"));
        step = 4; details.Text = "✓ 后台程序与游戏 VPK 已成套更新\r\n✓ 个人设置、密钥与证书已保留\r\n✓ Steam 启动项已检查"; Draw(); CaptureImage(Variant("complete"));
        step = 0; MinimumSize = Size.Empty; ClientSize = new Size(900, 610); Draw(); CaptureImage(Variant("small"));
        if(new Control[]{allowReadOnly,repairMissing,repairGameVpk}.Any(c=>c.IsDisposed))throw new InvalidOperationException("Shared repair controls were disposed while switching installer pages.");
        Close();
    }
    public async Task RunStartupCheck(string folder)
    {
        suppressDialogs = true; ShowOffscreen(); await Prepare();
        CaptureImage(Path.Combine(folder, "startup.png"));
        var result = new
        {
            ok = ready, error = errorMessage, version = InstallerVersion.Current,
            gameCandidates = Items(Field(snapshot, "games")).Count(), steamAccounts = Items(Field(snapshot, "users")).Count(),
            certificateReady = Flag(Field(snapshot, "certificate"), "ready"), installed = false,
            labelsEnabled = sections.SelectMany(s => s.Controls.Cast<Control>()).OfType<Label>().All(l => l.Enabled),
            disabledButtonText = previous.DisplayTextColor.ToArgb()
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "startup-check.json"), JsonSerializer.Serialize(result));
        Close();
    }
}
