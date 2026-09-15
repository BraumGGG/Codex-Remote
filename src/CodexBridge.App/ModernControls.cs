// =============================================================================
// Codex Remote · WinForms 11 + Fluent Modern 控件库
// =============================================================================
// 目标框架：.NET 8 + WinForms 11
// 文件：单一 .cs 文件，复制到项目根目录即可使用
// 配套设计稿：design-system.html / w-*.html
// 解决 5 个实现 gap：
//   1. DataGridView 80px 行高 → RowTemplate.Height = 36
//   2. Button 文字换行（"权"）→ AutoSize + MinimumSize.Width >= 80
//   3. 状态文字无色块 → PillLabel (OnPaint 圆角彩色徽章)
//   4. 输入框无 focus glow → ModernTextBox OnPaint 3px brand-12% 光晕
//   5. Menu/ToolStrip 裸文字 → ModernToolStripRenderer (圆角 8 + 品牌色高亮)
//
// 使用方式（Program.cs）：
//   Application.SetHighDpiMode(HighDpiMode.DpiUnawareGdiScaled);
//   Application.EnableVisualStyles();
//   Application.SetCompatibleTextRenderingDefault(false);
//   Application.Run(new SampleOverviewForm());
//
// 然后在 Form 构造函数顶部加一行：
//   MenuStrip = new ModernMenuStrip();   // 替换默认 MenuStrip
//   StatusStrip = new ModernStatusStrip(); // 替换默认 StatusStrip
//   ToolStripManager.Renderer = new ModernToolStripRenderer();
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CodexRemote.UI
{
    // =========================================================================
    // 1) Brand tokens — 与 design-system.html / wf.css 完全一致
    // =========================================================================
    public static class Brand
    {
        // —— Brand (Indigo) ——
        public static readonly Color Brand50   = Color.FromArgb(238, 240, 255);
        public static readonly Color Brand100  = Color.FromArgb(224, 227, 254);
        public static readonly Color Brand200  = Color.FromArgb(199, 203, 254);
        public static readonly Color Brand500  = Color.FromArgb( 99, 102, 241);  // #6366F1 主色
        public static readonly Color Brand600  = Color.FromArgb( 79,  82, 226);
        public static readonly Color Brand700  = Color.FromArgb( 67,  70, 217);

        // —— Pro (Purple) ——
        public static readonly Color Pro50     = Color.FromArgb(245, 240, 255);
        public static readonly Color Pro100    = Color.FromArgb(232, 220, 255);
        public static readonly Color Pro500    = Color.FromArgb(124,  58, 237);

        // —— Surface ——
        public static readonly Color Surface      = Color.White;
        public static readonly Color Surface2     = Color.FromArgb(248, 250, 252);
        public static readonly Color Canvas       = Color.FromArgb(246, 247, 249);
        public static readonly Color Border       = Color.FromArgb(226, 229, 235);
        public static readonly Color BorderStrong = Color.FromArgb(203, 207, 217);

        // —— Text ——
        public static readonly Color Text  = Color.FromArgb( 24,  28,  36);
        public static readonly Color Text2 = Color.FromArgb( 72,  78,  92);
        public static readonly Color Text3 = Color.FromArgb(126, 132, 146);
        public static readonly Color Text4 = Color.FromArgb(156, 162, 176);

        // —— Status ——
        public static readonly Color Success500 = Color.FromArgb( 34, 175, 110);
        public static readonly Color Success50  = Color.FromArgb(230, 247, 238);
        public static readonly Color Warning500 = Color.FromArgb(212, 158,  46);
        public static readonly Color Warning50  = Color.FromArgb(254, 248, 232);
        public static readonly Color Danger500  = Color.FromArgb(218,  76,  76);
        public static readonly Color Danger50   = Color.FromArgb(253, 231, 233);
        public static readonly Color Info500    = Color.FromArgb( 56, 132, 232);
        public static readonly Color Info50     = Color.FromArgb(232, 242, 254);
        public static readonly Color Neutral500 = Color.FromArgb(120, 128, 144);
        public static readonly Color Neutral50  = Color.FromArgb(241, 243, 247);

        // —— Title bar (WinForms 11 light) ——
        public static readonly Color SysBtnHover      = Color.FromArgb(  8,   0,   0,   0);
        public static readonly Color SysBtnCloseHover = Color.FromArgb(232,  17,  35);
    }

    // =========================================================================
    // 2) Draw 工具：圆角路径、阴影（公开给业务表单用）
    // =========================================================================
    public static class Draw
    {
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle r, int radius, Color fill)
        {
            using var path = RoundedRect(r, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        public static void StrokeRounded(Graphics g, Rectangle r, int radius, Color stroke, float width = 1f)
        {
            using var path = RoundedRect(r, radius);
            using var pen = new Pen(stroke, width);
            g.DrawPath(pen, path);
        }

        public static void DrawCardShadow(Graphics g, Rectangle bounds, int radius)
        {
            // 设计稿：d-shadow-sm = 0 1 2 rgba(0,0,0,.04), 0 1 3 rgba(0,0,0,.06)
            // 用 2 层半透明圆角模拟
            using (var p1 = RoundedRect(new Rectangle(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height), radius))
            using (var b1 = new SolidBrush(Color.FromArgb(10, 0, 0, 0)))
                g.FillPath(b1, p1);
            using (var p2 = RoundedRect(new Rectangle(bounds.X, bounds.Y + 2, bounds.Width, bounds.Height), radius))
            using (var b2 = new SolidBrush(Color.FromArgb(15, 0, 0, 0)))
                g.FillPath(b2, p2);
        }

        public static StringFormat CenterSF() => new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
    }

    // =========================================================================
    // 3) ModernButton — 圆角 6 / Primary / Danger / 三态
    // =========================================================================
    [ToolboxBitmap(typeof(Button))]
    public class ModernButton : Button
    {
        public enum Variant { Default, Primary, Danger }

        private int _radius = 6;
        private Variant _variant = Variant.Default;
        private bool _hover, _pressed;

        [DefaultValue(6)] public int Radius { get => _radius; set { _radius = value; Invalidate(); } }
        [DefaultValue(Variant.Default)]
        public Variant ButtonVariant { get => _variant; set { _variant = value; Invalidate(); } }

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            BackColor = Brand.Surface;
            ForeColor = Brand.Text;
            Font = new Font("Segoe UI", 9F);
            Size = new Size(80, 28);
            MinimumSize = new Size(80, 28);  // 关键：避免"权"被截断
            AutoSize = true;                  // 关键：让按钮自适应文字
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12, 0, 12, 0);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true;  Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)   { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 颜色
            Color fill, border, text;
            switch (_variant)
            {
                case Variant.Primary:
                    fill   = _pressed ? Brand.Brand700 : (_hover ? Brand.Brand600 : Brand.Brand500);
                    border = fill;
                    text   = Color.White;
                    break;
                case Variant.Danger:
                    fill   = _pressed ? Color.FromArgb(180, 60, 60) : (_hover ? Brand.Danger500 : Color.FromArgb(218, 76, 76));
                    border = fill;
                    text   = Color.White;
                    break;
                default:
                    fill   = _pressed ? Brand.Surface2 : (_hover ? Brand.Surface2 : Brand.Surface);
                    border = Brand.Border;
                    text   = Enabled ? Brand.Text : Brand.Text4;
                    break;
            }

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRounded(g, rect, _radius, fill);
            Draw.StrokeRounded(g, rect, _radius, border);

            // 文字（手动画，因为 UserPaint 不再用系统文字）
            using var sf = Draw.CenterSF();
            sf.FormatFlags = StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            using var brush = new SolidBrush(text);
            g.DrawString(Text, Font, brush, rect, sf);
        }
    }

    // =========================================================================
    // 4) PillLabel — 圆角彩色状态徽章（含小圆点）
    // =========================================================================
    [ToolboxBitmap(typeof(Label))]
    public class PillLabel : Label
    {
        public enum PillTone { Neutral, Brand, Pro, Success, Warning, Danger, Info }

        private PillTone _tone = PillTone.Brand;
        private bool _showDot = true;

        [DefaultValue(PillTone.Brand)]
        public PillTone Tone { get => _tone; set { _tone = value; Invalidate(); } }

        [DefaultValue(true)]
        public bool ShowDot { get => _showDot; set { _showDot = value; Invalidate(); } }

        public PillLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            AutoSize = true;
            Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Padding = new Padding(8, 2, 10, 2);
            BackColor = Color.Transparent;
            Text = "Pro";
        }

        private (Color bg, Color fg, Color dot) GetColors()
        {
            return _tone switch
            {
                PillTone.Brand   => (Brand.Brand50,  Brand.Brand700, Brand.Brand500),
                PillTone.Pro     => (Brand.Pro50,    Brand.Pro500,   Brand.Pro500),
                PillTone.Success => (Brand.Success50, Color.FromArgb(20, 130, 80), Brand.Success500),
                PillTone.Warning => (Brand.Warning50, Color.FromArgb(160, 110, 20), Brand.Warning500),
                PillTone.Danger  => (Brand.Danger50,  Brand.Danger500, Brand.Danger500),
                PillTone.Info    => (Brand.Info50,    Color.FromArgb(40, 90, 170), Brand.Info500),
                _                => (Brand.Neutral50, Brand.Text2,    Brand.Neutral500),
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var (bg, fg, dot) = GetColors();
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRounded(g, rect, 999, bg);  // 完全圆角

            int dotX = 7, dotY = (Height - 1) / 2;
            if (_showDot)
            {
                using var dotBrush = new SolidBrush(dot);
                g.FillEllipse(dotBrush, dotX - 3, dotY - 3, 6, 6);
            }

            int textX = _showDot ? dotX + 6 : 8;
            var textRect = new Rectangle(textX, 0, Width - textX - 6, Height);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            using var brush = new SolidBrush(fg);
            g.DrawString(Text, Font, brush, textRect, sf);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return new Size(text.Width + (_showDot ? 27 : 18), Math.Max(24, text.Height + 4));
        }
    }

    // =========================================================================
    // 5) ModernCard — 圆角 8 + 阴影 Panel
    // =========================================================================
    [ToolboxBitmap(typeof(Panel))]
    public class ModernCard : Panel
    {
        private int _radius = 8;
        private Color _borderColor = Brand.Border;

        [DefaultValue(8)] public int Radius { get => _radius; set { _radius = value; Invalidate(); } }
        [DefaultValue(typeof(Color), "226, 229, 235")]
        public Color BorderColor { get => _borderColor; set { _borderColor = value; Invalidate(); } }

        public ModernCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Brand.Surface;
            Padding = new Padding(20);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // 阴影（在自身外面画）
            Draw.DrawCardShadow(g, rect, _radius);
            // 卡片本体
            Draw.FillRounded(g, rect, _radius, BackColor == Color.Empty ? Brand.Surface : BackColor);
            Draw.StrokeRounded(g, rect, _radius, _borderColor);

            base.OnPaint(e);  // 让子控件正常画
        }
    }

    // =========================================================================
    // 6) ModernTextBox — 圆角 + 3px brand focus glow
    // =========================================================================
    [ToolboxBitmap(typeof(TextBox))]
    public class ModernTextBox : TextBox
    {
        private int _radius = 6;
        private bool _focused;

        [DefaultValue(6)] public int Radius { get => _radius; set { _radius = value; Invalidate(); } }

        public ModernTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BorderStyle = BorderStyle.None;     // 自绘边框
            BackColor = Brand.Surface;
            ForeColor = Brand.Text;
            Font = new Font("Segoe UI", 10F);
            Padding = new Padding(10, 8, 10, 8);
        }

        protected override void OnGotFocus(EventArgs e) { _focused = true;  Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _focused = false; Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // TextBox 自带 OnPaint 会画背景 + 光标，我们只画外壳
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            // 阴影 / 光晕
            if (_focused)
            {
                Draw.FillRounded(g, rect, _radius, Color.FromArgb(31, Brand.Brand500));  // 12% alpha
            }

            Draw.FillRounded(g, rect, _radius, BackColor);
            Draw.StrokeRounded(g, rect, _radius, _focused ? Brand.Brand500 : Brand.Border, _focused ? 1.5f : 1f);

            base.OnPaint(e);
        }

        // 默认高度太小，给个 32 让 Padding 生效
        protected override Size DefaultSize => new Size(200, 32);
    }

    // =========================================================================
    // 7) ModernDataGridView — 36px 行 + brand 选中 + 圆角列头
    // =========================================================================
    [ToolboxBitmap(typeof(DataGridView))]
    public class ModernDataGridView : DataGridView
    {
        public ModernDataGridView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);

            // 关键：行高 36（不是默认 22，也不是 dev 写的 80）
            RowTemplate.Height = 36;
            ColumnHeadersHeight = 32;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // 背景
            BackgroundColor = Brand.Canvas;
            GridColor = Brand.Border;
            BorderStyle = BorderStyle.None;

            // 单元格
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Brand.Surface,
                ForeColor = Brand.Text,
                SelectionBackColor = Brand.Brand50,         // 关键：选中用 brand-50
                SelectionForeColor = Brand.Brand700,
                Font = new Font("Segoe UI", 10F),
                Padding = new Padding(12, 0, 12, 0),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
            };

            // 列头
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Brand.Surface2,
                ForeColor = Brand.Text2,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
            };
            EnableHeadersVisualStyles = false;

            // 行
            RowHeadersVisible = false;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = false;

            // 行高模式
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        }
    }

    // =========================================================================
    // 8) ModernTabControl — 下划线样式（圆角顶部 brand 高亮）
    // =========================================================================
    [ToolboxBitmap(typeof(TabControl))]
    public class ModernTabControl : TabControl
    {
        public ModernTabControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            // 仅自绘 Tab 头部，TabPage 内容交给系统绘制。
            // 开启 UserPaint 会让 WinForms 不再绘制子页面，切换时容易出现空白或残影。
            DrawMode = TabDrawMode.OwnerDrawFixed;
            ItemSize = new Size(108, 36); // H 36, enough room for Chinese labels
            SizeMode = TabSizeMode.Fixed;
            Padding = new Point(0, 0);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var tab = TabPages[e.Index];
            var bounds = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            // 背景透明，页面区由父容器填白
            // tab 文字
            var textColor = selected ? Brand.Brand500 : Brand.Text2;
            using var brush = new SolidBrush(textColor);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(tab.Text, Font, brush, bounds, sf);

            // 选中下划线 2px brand
            if (selected)
            {
                using var pen = new Pen(Brand.Brand500, 2f);
                g.DrawLine(pen, bounds.Left + 4, bounds.Bottom - 1, bounds.Right - 4, bounds.Bottom - 1);
            }

            // tab 之间分隔
            using var sepPen = new Pen(Brand.Border, 1f);
            g.DrawLine(sepPen, bounds.Right, 8, bounds.Right, bounds.Bottom - 8);
        }

        // 不覆盖 OnPaint：系统会正确绘制 TabPage 内容，OnDrawItem 负责头部样式。
    }

    // =========================================================================
    // 9) ModernToolStripRenderer — MenuStrip / ToolStrip / StatusStrip 通用
    // =========================================================================
    public class ModernToolStripRenderer : ToolStripProfessionalRenderer
    {
        public ModernToolStripRenderer() : base(new ModernColorTable()) { }
        public ModernToolStripRenderer(ProfessionalColorTable table) : base(table) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // 子项背景：选中/悬停 → 圆角 6
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected || e.Item.Pressed || e.Item.IsOnDropDown)
            {
                var r = new Rectangle(0, 0, e.Item.Width - 1, e.Item.Height - 1);
                Draw.FillRounded(g, r, 6, Brand.Brand50);
            }
        }

        protected override void OnRenderItemBackground(ToolStripItemRenderEventArgs e)
        {
            // ToolStripButton 背景：选中/悬停 → brand
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ts = e.ToolStrip;
            if (ts is StatusStrip) return;  // 状态栏不画背景

            if (e.Item is ToolStripButton &&
                (e.Item.BackColor == Brand.Brand500 || e.Item.BackColor == Brand.Brand600))
            {
                var primary = new Rectangle(2, 2, Math.Max(0, e.Item.Width - 4), Math.Max(0, e.Item.Height - 4));
                Draw.FillRounded(g, primary, 6, e.Item.BackColor);
                return;
            }

            bool active = e.Item.Selected || e.Item.Pressed;
            if (active)
            {
                var r = new Rectangle(2, 2, e.Item.Width - 4, e.Item.Height - 4);
                Color fill = e.Item.Pressed ? Brand.Brand100 : Brand.Surface2;
                Color border = e.Item.Pressed ? Brand.Brand500 : Brand.Border;
                Draw.FillRounded(g, r, 6, fill);
                Draw.StrokeRounded(g, r, 6, border);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            // 工具栏背景：白
            using var bg = new SolidBrush(Brand.Surface);
            e.Graphics.FillRectangle(bg, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 底部分隔线
            using var pen = new Pen(Brand.Border, 1f);
            e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Bottom - 1,
                e.AffectedBounds.Width, e.AffectedBounds.Bottom - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Brand.Border, 1f);
            int x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, 4, x, e.Item.Height - 4);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Brand.Text3;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.BackColor == Brand.Brand500 || e.Item.BackColor == Brand.Brand600
                ? Color.White
                : e.Item.Enabled ? Brand.Text : Brand.Text4;
            base.OnRenderItemText(e);
        }

        // 颜色表
        private class ModernColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected        => Brand.Brand50;
            public override Color MenuItemBorder          => Brand.Brand200;
            public override Color MenuItemSelectedGradientBegin => Brand.Brand50;
            public override Color MenuItemSelectedGradientEnd   => Brand.Brand50;
            public override Color MenuItemPressedGradientBegin  => Brand.Brand100;
            public override Color MenuItemPressedGradientEnd    => Brand.Brand100;
            public override Color MenuBorder              => Brand.Border;
            public override Color ToolStripDropDownBackground => Brand.Surface;
            public override Color ImageMarginGradientBegin     => Brand.Surface;
            public override Color ImageMarginGradientEnd       => Brand.Surface;
            public override Color ImageMarginGradientMiddle    => Brand.Surface;
            public override Color StatusStripGradientBegin     => Brand.Surface;
            public override Color StatusStripGradientEnd       => Brand.Surface;
        }
    }

    // =========================================================================
    // 10) StatusDot — 单个状态点（用在 StatusStrip / Card 列表）
    // =========================================================================
    [ToolboxBitmap(typeof(Label))]
    public class StatusDot : Label
    {
        public enum DotTone { Success, Warning, Danger, Info, Neutral, Brand }

        private DotTone _tone = DotTone.Success;
        private bool _pulse = false;

        [DefaultValue(DotTone.Success)]
        public DotTone Tone { get => _tone; set { _tone = value; Invalidate(); } }

        [DefaultValue(false)]
        public bool Pulse { get => _pulse; set { _pulse = value; Invalidate(); } }

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            AutoSize = false;
            Size = new Size(10, 10);
            Text = "";
        }

        private Color GetColor() => _tone switch
        {
            DotTone.Success => Brand.Success500,
            DotTone.Warning => Brand.Warning500,
            DotTone.Danger  => Brand.Danger500,
            DotTone.Info    => Brand.Info500,
            DotTone.Brand   => Brand.Brand500,
            _               => Brand.Neutral500,
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = GetColor();

            int r = Math.Min(Width, Height) - 2;
            var rect = new Rectangle((Width - r) / 2, (Height - r) / 2, r, r);

            // 外光晕（模拟 pulse）
            if (_pulse)
            {
                using var halo = new SolidBrush(Color.FromArgb(60, c));
                g.FillEllipse(halo, rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4);
            }

            using var brush = new SolidBrush(c);
            g.FillEllipse(brush, rect);
        }
    }

    // =========================================================================
    // 11) IconGlyph — 用 GDI+ 画 Unicode/Symbol 图标（避免外部资源）
    // =========================================================================
    public static class IconGlyph
    {
        // Segoe Fluent Icons / Segoe MDL2 Assets 的 Unicode 点位
        // 实际使用前在 Windows 11 设备上验证，未安装字体时会回退到普通字符
        public const string Refresh     = "\uE72C";   // ↻
        public const string Pause       = "\uE769";   // ⏸
        public const string Play        = "\uE768";
        public const string Settings    = "\uE713";   // ⚙
        public const string Home        = "\uE80F";   // ⌂
        public const string Projects    = "\uE8B7";   // ▣
        public const string Devices     = "\uE968";   // ▢
        public const string Pair        = "\uE8A1";   // ▦
        public const string Diagnostic  = "\uE9F5";   // ⌁
        public const string Search      = "\uE721";
        public const string Close       = "\uE8BB";   // ✕
        public const string Minimize    = "\uE921";   // —
        public const string Maximize    = "\uE922";   // ▢
        public const string Restore     = "\uE923";
        public const string Check       = "\uE73E";   // ✓
        public const string Warning     = "\uE7BA";   // ⚠
        public const string Folder      = "\uE8B7";
        public const string Copy        = "\uE8C8";
        public const string More        = "\uE712";
        public const string ChevronRight = "\uE974";
        public const string ChevronDown = "\uE70D";
        public const string Shield      = "\uE72E";
        public const string Phone       = "\uE8EA";
        public const string Laptop      = "\uE7F8";
        public const string Network     = "\uE968";
        public const string Trash       = "\uE74D";
        public const string Link        = "\uE71B";
        public const string Add         = "\uE710";

        /// <summary>把 Unicode 字符渲染成 Image（用于 ToolStripButton.Image）</summary>
        public static Image Render(string glyph, int size = 16, Color? color = null)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using var font = new Font("Segoe MDL2 Assets", size * 0.7f, FontStyle.Regular);
                using var brush = new SolidBrush(color ?? Brand.Text);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }
    }
}
