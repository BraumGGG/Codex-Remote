using System.Drawing;
using CodexRemote.UI;

namespace CodexBridge.App;

internal static class UiTheme
{
    public static readonly Color Canvas = Brand.Canvas;
    public static readonly Color Surface = Brand.Surface;
    public static readonly Color SurfaceMuted = Brand.Surface2;
    public static readonly Color Sidebar = Brand.Surface;
    public static readonly Color SidebarSelected = Brand.Brand50;
    public static readonly Color Text = Brand.Text;
    public static readonly Color TextSecondary = Brand.Text2;
    public static readonly Color Muted = Brand.Text3;
    public static readonly Color Border = Brand.Border;
    public static readonly Color Accent = Brand.Brand500;
    public static readonly Color AccentSoft = Brand.Brand50;
    public static readonly Color InfoTint = Brand.Info50;
    public static readonly Color AccentText = Brand.Brand700;
    public static readonly Color Success = Brand.Success500;
    public static readonly Color SuccessTint = Brand.Success50;
    public static readonly Color SuccessSoft = Brand.Success50;
    public static readonly Color Warning = Brand.Warning500;
    public static readonly Color WarningTint = Brand.Warning50;
    public static readonly Color WarningSoft = Brand.Warning50;
    public static readonly Color Danger = Brand.Danger500;

    // WinForms sizes are points; these values map closely to the prototype's
    // 20px headings, 13px body text and 11px secondary labels at 96 DPI.
    public static readonly Font TitleFont = new("Segoe UI", 15f, FontStyle.Bold);
    public static readonly Font SectionFont = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font BodyFont = new("Segoe UI", 9f);
    public static readonly Font LabelFont = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font SmallFont = new("Segoe UI", 8.25f);
    public static readonly Font IconFont = new("Segoe MDL2 Assets", 11f);
    public static readonly Font MonoFont = new("Cascadia Mono", 9f);
}
