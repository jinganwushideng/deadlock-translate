using System.Drawing.Drawing2D;

namespace DeadlockTranslate.Setup;

// The operating system still owns text selection, input, IME, dropdown and keyboard
// navigation. Only the outer border and the dropdown button are painted here.
internal sealed class NativeComboBox : ComboBox {
 public NativeComboBox(){FlatStyle=FlatStyle.Flat;BackColor=Color.FromArgb(78,76,66);ForeColor=InstallerColors.Ink;DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=28;IntegralHeight=false;}
 protected override void OnDrawItem(DrawItemEventArgs e){if(e.Index<0)return;var chosen=(e.State&DrawItemState.Selected)!=0;using var fill=new SolidBrush(chosen?Color.FromArgb(101,111,87):Color.FromArgb(67,66,58));e.Graphics.FillRectangle(fill,e.Bounds);TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-12,e.Bounds.Height),InstallerColors.Ink,TextFormatFlags.VerticalCenter|TextFormatFlags.Left);}
 protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg is 0x000F or 0x0085 or 0x0086){using var g=Graphics.FromHwnd(Handle);PaintChrome(g);}else if(m.Msg is 0x0317 or 0x0318 && m.WParam!=IntPtr.Zero){using var g=Graphics.FromHdc(m.WParam);PaintChrome(g);}}
 void PaintChrome(Graphics g){g.SmoothingMode=SmoothingMode.AntiAlias;var box=new Rectangle(Width-31,0,31,Height);using var fill=new SolidBrush(Enabled?Color.FromArgb(78,76,66):Color.FromArgb(51,54,46));g.FillRectangle(fill,box);using var arrow=new Pen(Color.FromArgb(204,197,177),2.3f);var x=Width-15;var y=Height/2;g.DrawLines(arrow,[new(x-5,y-3),new(x,y+3),new(x+5,y-3)]);using var border=new Pen(Color.FromArgb(78,76,66),3);g.DrawRectangle(border,1,1,Width-3,Height-3);}
}
internal sealed class RoundedField : Panel {
 public Control Input {get;}
 public RoundedField(Control input){Input=input;Height=46;Padding=new Padding(7,7,7,7);Margin=new Padding(0,0,0,5);BackColor=Color.FromArgb(78,76,66);input.Location=new Point(7,8);input.BackColor=BackColor;input.ForeColor=InstallerColors.Ink;if(input is TextBox t)t.BorderStyle=BorderStyle.None;Controls.Add(input);SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.ResizeRedraw,true);Resize+=(_,_)=>{input.Width=Math.Max(20,Width-14);input.Height=Math.Max(20,Height-14);using var rounded=InstallerColors.Rounded(new RectangleF(0,0,Width,Height),8);var previous=Region;Region=new Region(rounded);previous?.Dispose();};}
 protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using var path=InstallerColors.Rounded(new RectangleF(1,1,Width-3,Height-3),8);using var pen=new Pen(Color.FromArgb(160,151,131),2);e.Graphics.DrawPath(pen,path);}
}
internal static class InstallerTexture {
 public static readonly Bitmap Grain=Build();
 static Bitmap Build(){var b=new Bitmap(180,180);var random=new Random(24);using var g=Graphics.FromImage(b);g.Clear(Color.FromArgb(0,0,0,0));for(var i=0;i<4500;i++){using var ink=new SolidBrush(i%2==0?Color.FromArgb(7,240,233,209):Color.FromArgb(9,10,14,10));g.FillRectangle(ink,random.Next(180),random.Next(180),1,1);}return b;}
}
