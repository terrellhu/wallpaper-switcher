using System.Drawing.Drawing2D;

namespace WallpaperSwitcher;

internal static class Palette
{
    public static readonly Color Canvas = Color.FromArgb(243, 246, 250);
    public static readonly Color Ink = Color.FromArgb(28, 43, 67);
    public static readonly Color Muted = Color.FromArgb(113, 127, 149);
    public static readonly Color Accent = Color.FromArgb(49, 107, 225);
    public static readonly Color Border = Color.FromArgb(226, 232, 241);
    public static readonly Color Soft = Color.FromArgb(237, 243, 255);
    public static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        DoubleBuffered = true;
        BackColor = Palette.Canvas;
        Padding = new Padding(20);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Palette.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 16);
        using var fill = new SolidBrush(Color.White);
        using var pen = new Pen(Palette.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class ModernButton : Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }
    private bool hover;
    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand; Height = 40; BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = !Enabled ? Color.FromArgb(239, 242, 247) : Primary
            ? (hover ? Color.FromArgb(37, 89, 199) : Palette.Accent) : hover ? Palette.Soft : Color.White;
        using var path = Palette.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(Primary && Enabled ? color : Palette.Border);
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            !Enabled ? Palette.Muted : Primary ? Color.White : Palette.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
    }
}

internal sealed class WallpaperPreview : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Image? Picture { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Size OriginalSize { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public WallpaperFit FitMode { get; set; } = WallpaperFit.Fill;
    public WallpaperPreview() { DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 40 || Height < 40) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var backdrop = Palette.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12);
        using var dark = new SolidBrush(Color.FromArgb(18, 29, 48));
        g.FillPath(dark, backdrop);
        Size screen = FitMode == WallpaperFit.Span ? SystemInformation.VirtualScreen.Size
            : Screen.PrimaryScreen?.Bounds.Size ?? new Size(1920, 1080);
        double ratio = (double)screen.Width / screen.Height;
        float w = Width - 38, h = (float)(w / ratio);
        if (h > Height - 36) { h = Height - 36; w = (float)(h * ratio); }
        var view = new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
        using var black = new SolidBrush(Color.Black);
        g.FillRectangle(black, view);
        var saved = g.Save();
        g.SetClip(view);
        if (Picture is not null && OriginalSize.Width > 0)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float sw = OriginalSize.Width, sh = OriginalSize.Height;
            float scale = FitMode switch
            {
                WallpaperFit.Fit => Math.Min(view.Width / sw, view.Height / sh),
                WallpaperFit.Center or WallpaperFit.Tile => view.Width / screen.Width,
                _ => Math.Max(view.Width / sw, view.Height / sh)
            };
            float dw = FitMode == WallpaperFit.Stretch ? view.Width : sw * scale;
            float dh = FitMode == WallpaperFit.Stretch ? view.Height : sh * scale;
            if (FitMode == WallpaperFit.Tile)
            {
                // Use a texture brush so tiny tiles do not require thousands of drawing calls.
                using var texture = new TextureBrush(Picture, WrapMode.Tile);
                texture.TranslateTransform(view.X, view.Y);
                texture.ScaleTransform(dw / Picture.Width, dh / Picture.Height);
                g.FillRectangle(texture, view);
            }
            else g.DrawImage(Picture, new RectangleF(view.X + (view.Width - dw) / 2, view.Y + (view.Height - dh) / 2, dw, dh));
        }
        else
        {
            using var text = new SolidBrush(Color.FromArgb(155, 174, 203));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("选择图片，预览桌面效果", Font, text, view, format);
        }
        g.Restore(saved);
        using var edge = new Pen(Color.FromArgb(64, 84, 113));
        g.DrawRectangle(edge, view.X, view.Y, view.Width, view.Height);
    }
}
