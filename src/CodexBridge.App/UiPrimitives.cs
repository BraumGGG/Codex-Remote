using System.Drawing.Drawing2D;
using CodexRemote.UI;

namespace CodexBridge.App;

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
}

internal sealed class EmptyStatePanel : Panel
{
    public string Glyph { get; set; } = IconGlyph.Folder;
    public string Title { get; set; } = "暂无内容";
    public string Description { get; set; } = "完成配对或添加项目后，内容会显示在这里。";
    public Button? ActionButton { get; set; }

    public EmptyStatePanel()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Padding = new Padding(24);
        MinimumSize = new Size(0, 164);
        Resize += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var center = ClientRectangle;
        var iconBounds = new Rectangle(center.X + (center.Width - 42) / 2, 18, 42, 42);
        using (var bg = new SolidBrush(UiTheme.AccentSoft))
        using (var path = RoundedPanelPath(iconBounds, 12))
            g.FillPath(bg, path);
        using (var iconBrush = new SolidBrush(UiTheme.Accent))
        using (var iconFont = new Font("Segoe MDL2 Assets", 20f))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(Glyph, iconFont, iconBrush, iconBounds, sf);

        var titleBounds = new Rectangle(12, 70, Math.Max(0, Width - 24), 24);
        TextRenderer.DrawText(g, Title, UiTheme.LabelFont, titleBounds, UiTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var descriptionBounds = new Rectangle(24, 96, Math.Max(0, Width - 48), 40);
        TextRenderer.DrawText(g, Description, UiTheme.SmallFont, descriptionBounds, UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPanelPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    private int _cornerRadius = 12;
    private Color _borderColor = UiTheme.Border;
    private int _borderWidth = 1;

    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(0, value); UpdateRegion(); Invalidate(); }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set { _borderColor = value; Invalidate(); }
    }

    public int BorderWidth
    {
        get => _borderWidth;
        set { _borderWidth = Math.Max(0, value); Invalidate(); }
    }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderWidth == 0 || Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreatePath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = CreatePath(ClientRectangle, CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class NavigationButton : Button
{
    public string Glyph { get; init; } = "";
    public string? Badge { get; set; }
    public bool Selected { get; set; }
    public bool Collapsed { get; set; }

    public NavigationButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        DoubleBuffered = true;
        UseVisualStyleBackColor = false;
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Selected ? UiTheme.SidebarSelected : BackColor;
        using (var brush = new SolidBrush(fill))
        using (var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            e.Graphics.FillPath(brush, path);

        var iconBounds = new Rectangle(Collapsed ? (Width - 24) / 2 : 14, 0, 24, Height);
        TextRenderer.DrawText(e.Graphics, Glyph, UiTheme.IconFont, iconBounds, Selected ? UiTheme.Text : UiTheme.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        if (Collapsed) return;
        var textBounds = new Rectangle(50, 0, Math.Max(0, Width - 92), Height);
        TextRenderer.DrawText(e.Graphics, Text, Selected ? UiTheme.LabelFont : UiTheme.BodyFont, textBounds,
            Selected ? UiTheme.Text : UiTheme.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (!string.IsNullOrWhiteSpace(Badge))
        {
            var badgeBounds = new Rectangle(Width - 38, (Height - 22) / 2, 26, 22);
            using var badgeBrush = new SolidBrush(Selected ? UiTheme.AccentSoft : UiTheme.SurfaceMuted);
            using var badgePath = RoundedPath(badgeBounds, 11);
            e.Graphics.FillPath(badgeBrush, badgePath);
            TextRenderer.DrawText(e.Graphics, Badge, UiTheme.SmallFont, badgeBounds,
                Selected ? UiTheme.Accent : UiTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
