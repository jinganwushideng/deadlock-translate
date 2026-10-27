using System.Text.Json;

namespace DeadlockTranslate.Setup;

public sealed class ReleaseNotesWindow : Form
{
    readonly ReleaseHistory history;
    readonly ComboBox versions = new NativeComboBox() { Name = "releaseVersions", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly RichTextBox body = new() { Name = "releaseBody", ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical };
    public string SelectedVersion => ((ReleaseEntry)versions.SelectedItem!).Version;
    public string RenderedText => body.Text;
    public int VersionCount => versions.Items.Count;

    public ReleaseNotesWindow(bool announcement=false)
    {
        history = ReleaseHistory.Load();
        Text = announcement?"同频 · 本次更新与反馈群":"同频 · 更新日志与反馈群"; Font = new Font("Microsoft YaHei UI", 10);
        BackColor = InstallerColors.Paper; ForeColor = InstallerColors.Ink;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 650); MinimumSize = new Size(620, 440); StartPosition = FormStartPosition.CenterParent;
        var area=Screen.PrimaryScreen?.WorkingArea??new Rectangle(0,0,1120,800);Size=new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(26, 22, 26, 20) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize)); main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize)); main.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(new Label { Text = announcement?"本次更新":"更新日志", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        main.Controls.Add(new Label { Text = "当前安装包 v" + InstallerVersion.Current + " · 内置 " + history.Entries.Count + " 个版本记录，离线可看", AutoSize = true, ForeColor = InstallerColors.Muted, Margin = new Padding(0, 0, 0, 18) }, 0, 1);
        versions.Margin = new Padding(0, 0, 0, 16); versions.Font = Font; versions.BackColor = InstallerColors.Card; versions.ForeColor = InstallerColors.Ink;
        foreach (var entry in history.Entries) versions.Items.Add(entry);
        versions.SelectedIndexChanged += (_, _) => { body.Text = ((ReleaseEntry)versions.SelectedItem!).DisplayText; body.SelectionStart = 0; body.SelectionLength = 0; body.ScrollToCaret(); };
        main.Controls.Add(versions, 0, 2);
        var card = new Panel { Dock = DockStyle.Fill, BackColor = InstallerColors.Card, Padding = new Padding(18), Margin = Padding.Empty };
        body.Font = Font; body.BackColor = InstallerColors.Card; body.ForeColor = InstallerColors.Ink; body.TabIndex = 1; card.Controls.Add(body);
        var columns=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,234));columns.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        columns.Controls.Add(card,0,0);columns.Controls.Add(CommunityPanel(),1,0);main.Controls.Add(columns,0,3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 14, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = "功能简记；完整记录随运行包 CHANGELOG.md 提供。", AutoSize = true, ForeColor = InstallerColors.Muted, Anchor = AnchorStyles.Left, Font = new Font(Font.FontFamily, 8.5f) }, 0, 0);
        var close = new ActionButton(ButtonKind.Primary) { Text = announcement?"知道了，继续":"关闭", Font = Font, MinimumSize = new Size(120, 40), Margin = Padding.Empty, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close(); footer.Controls.Add(close, 1, 0); main.Controls.Add(footer, 0, 4);
        Controls.Add(main); CancelButton = close; versions.SelectedIndex = 0;
    }
    private Control CommunityPanel(){
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=new Padding(16,0,0,0),Padding=new Padding(12),BackColor=InstallerColors.Card};
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(var i=0;i<4;i++)panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label{Text="反馈与及时更新",AutoSize=true,ForeColor=InstallerColors.Ink,Font=new Font(Font,FontStyle.Bold),Margin=new Padding(0,0,0,8)},0,0);
        panel.Controls.Add(new Label{Text="QQ 群 923443423",AutoSize=true,ForeColor=InstallerColors.Ink,Margin=new Padding(0,0,0,6)},0,1);
        panel.Controls.Add(new Label{Text="加入群聊反馈问题，获取及时更新。",AutoSize=true,MaximumSize=new Size(190,0),ForeColor=InstallerColors.Muted,Margin=new Padding(0,0,0,8)},0,2);
        var copy=new ActionButton{Text="复制群号",Dock=DockStyle.Top,Height=34,Margin=new Padding(0,0,0,8)};
        copy.Click+=(_,_)=>{try{Clipboard.SetText("923443423");copy.Text="已复制 923443423";}catch{copy.Text="群号：923443423";}};panel.Controls.Add(copy,0,3);
        var picture=new QrThumbnail{Name="communityQr",Dock=DockStyle.Fill,Cursor=Cursors.Hand,Margin=Padding.Empty};
        using(var stream=typeof(ReleaseNotesWindow).Assembly.GetManifestResourceStream("community-qr.png")){
            if(stream!=null){using var original=Image.FromStream(stream);picture.Image=new Bitmap(original);}
        }
        picture.Click+=(_,_)=>ShowQr();panel.Controls.Add(picture,0,4);
        var large=new ActionButton{Text="查看二维码大图",Dock=DockStyle.Top,Height=32,Margin=new Padding(0,6,0,0)};large.Click+=(_,_)=>ShowQr();panel.Controls.Add(large,0,5);
        panel.Resize+=(_,_)=>picture.Visible=panel.ClientSize.Height>=330;
        return panel;
    }
    private sealed class QrThumbnail:Control {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Image? Image {get;set;}
        public QrThumbnail(){DoubleBuffered=true;Resize+=(_,_)=>Invalidate();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(Image==null)return;
            var size=Math.Max(1,Math.Min(ClientSize.Width,ClientSize.Height));var destination=new Rectangle((ClientSize.Width-size)/2,(ClientSize.Height-size)/2,size,size);
            // Viewport into the provided portrait card, with quiet margins around the code.
            // The embedded original is preserved byte-for-byte and opens in the large view.
            var source=new RectangleF(Image.Width*.13f,Image.Height*.32f,Image.Width*.74f,Image.Height*.417f);
            e.Graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;e.Graphics.DrawImage(Image,destination,source,GraphicsUnit.Pixel);
        }
        protected override void Dispose(bool disposing){if(disposing){Image?.Dispose();Image=null;}base.Dispose(disposing);}
    }
    private void ShowQr(){
        using var popup=new Form{Text="QQ 群 923443423 · 扫码反馈问题与获取更新",StartPosition=FormStartPosition.CenterParent,BackColor=InstallerColors.Card,ShowInTaskbar=false};
        var area=Screen.FromControl(this).WorkingArea;popup.ClientSize=new Size(Math.Min(550,area.Width-40),Math.Min(950,area.Height-80));
        using var stream=typeof(ReleaseNotesWindow).Assembly.GetManifestResourceStream("community-qr.png");if(stream==null)return;
        using var source=Image.FromStream(stream);using var image=new Bitmap(source);
        var picture=new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=image};popup.Controls.Add(picture);popup.ShowDialog(this);
    }
    public void SelectVersion(string version)
    {
        versions.SelectedItem = history.Entries.Single(e => e.Version == version);
    }
    protected override void OnShown(EventArgs e){base.OnShown(e);var area=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));Size=new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));}
    public static void SavePreviews(string folder,bool announcement=false)
    {
        Directory.CreateDirectory(folder);
        using var window = new ReleaseNotesWindow(announcement) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        window.Show();
        void Save(string name)
        {
            window.PerformLayout(); Application.DoEvents();
            using var bitmap = new Bitmap(window.Width, window.Height); window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save(Path.Combine(folder, name + ".png"));
        }
        Save("current"); window.SelectVersion("0.6.0"); Save("history");
        window.MinimumSize = Size.Empty; window.ClientSize = new Size(600, 420); Save("small");
        File.WriteAllText(Path.Combine(folder, "release-history-preview.json"), JsonSerializer.Serialize(new { version = InstallerVersion.Current, versions = window.VersionCount, selected = window.SelectedVersion, historyPresent = window.RenderedText.Contains("多语言收发"), installed = false, shellStarted = false }));
        window.Close();
    }
}
