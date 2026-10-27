using System.Drawing.Drawing2D;

namespace DeadlockTranslate.Setup;

internal static class InstallerColors
{
    public static readonly Color Paper = Color.FromArgb(20, 20, 20);
    public static readonly Color Card = Color.FromArgb(88, 88, 88);
    public static readonly Color Ink = Color.FromArgb(241, 237, 226);
    public static readonly Color Muted = Color.FromArgb(209, 203, 185);
    public static readonly Color Border = Color.FromArgb(122, 118, 104);
    public static readonly Color Gold = Color.FromArgb(119, 234, 153);
    public static readonly Color Sidebar = Color.FromArgb(20, 20, 20);
    public static readonly Color Cream = Color.FromArgb(237, 230, 213);
    public static readonly Color DisabledText = Color.FromArgb(171, 173, 160);
    public static readonly Color DisabledFill = Color.FromArgb(53, 57, 51);

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum ButtonKind { Secondary, Primary, Quiet, Danger }

// Native Button semantics preserve keyboard activation, focus and accessibility.
// Painting the disabled state ourselves avoids Windows drawing black text on dark fills.
internal sealed class ActionButton : Button
{
    bool hovered, pressed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    public ActionButton(ButtonKind kind = ButtonKind.Secondary)
    {
        Kind = kind;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        AutoSize = true;
        Padding = new Padding(15, 8, 15, 8);
        Margin = new Padding(0, 0, 8, 0);
        MinimumSize = new Size(96, 40);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + 4), Math.Max(MinimumSize.Height, text.Height + Padding.Vertical));
    }

    public Color DisplayTextColor => !Enabled ? InstallerColors.DisabledText : Kind == ButtonKind.Quiet ? InstallerColors.Cream : Kind == ButtonKind.Danger ? Color.FromArgb(255, 180, 168) : Color.FromArgb(38, 38, 32);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Paint every pixel around the rounded shape ourselves. Button's native
        // background can leave its rectangular border behind after a repaint.
        e.Graphics.Clear(Parent?.BackColor ?? InstallerColors.Paper);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? InstallerColors.Paper);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = !Enabled ? InstallerColors.DisabledFill : Kind == ButtonKind.Primary ? Color.FromArgb(237,230,213) : Kind == ButtonKind.Quiet ? InstallerColors.Paper : Kind == ButtonKind.Danger ? Color.FromArgb(89,53,47) : Color.FromArgb(215,208,190);
        if (Enabled && (hovered || pressed)) fill = Kind == ButtonKind.Quiet ? Color.FromArgb(51, 54, 47) : Kind == ButtonKind.Danger ? Color.FromArgb(103, 62, 54) : Color.FromArgb(239, 233, 216);
        using var shape = InstallerColors.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), Height / 2f);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(!Enabled ? InstallerColors.Border : Kind == ButtonKind.Primary ? InstallerColors.Sidebar : InstallerColors.Border);
        e.Graphics.FillPath(brush, shape);
        e.Graphics.DrawPath(pen, shape);
        using var inset=InstallerColors.Rounded(new RectangleF(4,4,Width-9,Height-9), Height/2f);
        using var insetPen=new Pen(Color.FromArgb(131,123,106),1);
        e.Graphics.DrawPath(insetPen,inset);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, DisplayTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
        if (Focused && ShowFocusCues)
        {
            using var focus = InstallerColors.Rounded(new RectangleF(4, 4, Width - 9, Height - 9), 5);
            using var focusPen = new Pen(Kind == ButtonKind.Quiet ? InstallerColors.Gold : Color.FromArgb(40,56,43), 1.5f);
            e.Graphics.DrawPath(focusPen, focus);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class SectionPanel : FlowLayoutPanel
{
    public SectionPanel()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 18);
        Margin = new Padding(0, 0, 0, 16);
        BackColor = InstallerColors.Card;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(InstallerColors.Paper);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = InstallerColors.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 1);
        using var brush = new SolidBrush(InstallerColors.Card);
        using var pen = new Pen(InstallerColors.Border);
        e.Graphics.FillPath(brush, shape); using var texture=new TextureBrush(InstallerTexture.Grain);e.Graphics.FillPath(texture,shape);
    }
}

internal sealed class StepNavigation : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Step { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Busy { get; set; }
    public StepNavigation() { Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = InstallerColors.Sidebar; ForeColor = InstallerColors.Cream; DoubleBuffered = true; TabStop = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Geometry follows the actual allocated pixels; font rendering already
        // accounts for monitor DPI. Scaling both clipped the sidebar at 150%.
        int X(int v) => v;
        using var brandFont = new Font("Microsoft YaHei UI", 24, FontStyle.Bold);
        using var smallFont = new Font("Segoe UI", 8.4f, FontStyle.Regular);
        using var labelFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular);
        using var numberFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var cream = new SolidBrush(InstallerColors.Cream);
        using var soft = new SolidBrush(Color.FromArgb(167, 178, 164));
        using var gold = new SolidBrush(Color.FromArgb(119, 234, 153));
        e.Graphics.DrawString("同频", brandFont, cream, X(27), X(32));
        e.Graphics.DrawString("DEADLOCK", smallFont, gold, X(30), X(87));
        e.Graphics.DrawString("TRANSLATE", smallFont, gold, X(30), X(103));
        using var line = new Pen(Color.FromArgb(67, 75, 68));
        e.Graphics.DrawLine(line, X(28), X(124), Width - X(28), X(124));
        string[] labels = ["证书准备", "安装位置", "检查与预览", "执行安装", "完成"];
        for (int i = 0; i < labels.Length; i++)
        {
            var y = X(162 + i * 62);
            var circle = new Rectangle(X(28), y, X(29), X(29));
            using var fill = new SolidBrush(i == Step ? Color.FromArgb(119, 234, 153) : Color.FromArgb(52, 61, 54));
            e.Graphics.FillEllipse(fill, circle);
            TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), numberFont, circle, i == Step ? InstallerColors.Sidebar : InstallerColors.Cream, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Graphics.DrawString(labels[i], labelFont, i == Step ? cream : soft, X(70), y + X(3));
            if (i < 4) e.Graphics.DrawLine(line, X(42), y + X(34), X(42), y + X(54));
        }
        var bottom = Math.Max(X(510), Height - X(108));
        e.Graphics.DrawLine(line, X(28), bottom, Width - X(28), bottom);
        e.Graphics.DrawString("安装 / 更新 / 维护", labelFont, cream, X(28), bottom + X(19));
        e.Graphics.DrawString("v" + InstallerVersion.Current + " · " + BuildInfo.Id.Split('-').Last()[..8], smallFont, soft, X(29), bottom + X(51));
    }
}
