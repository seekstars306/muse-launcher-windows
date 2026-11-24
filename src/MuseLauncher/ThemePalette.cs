using System.Windows.Media;

namespace MuseLauncher;

/// <summary>日间 / 夜间模式统一调色板。</summary>
public sealed class ThemePalette
{
    public bool IsDark { get; init; }
    public Brush WindowBg { get; init; } = Brushes.Black;
    public Brush RailBg { get; init; } = Brushes.Black;
    public Brush DrawerBg { get; init; } = Brushes.Black;
    public Brush SurfaceBg { get; init; } = Brushes.Black;
    public Brush CardBg { get; init; } = Brushes.Black;
    public Brush InputBg { get; init; } = Brushes.Black;
    public Brush RowBg { get; init; } = Brushes.Black;
    public Brush RowActiveBg { get; init; } = Brushes.Black;
    public Brush TileBg { get; init; } = Brushes.Black;
    public Brush BorderBrush { get; init; } = Brushes.Gray;
    public Brush ButtonBg { get; init; } = Brushes.Gray;
    public Brush ButtonHoverBg { get; init; } = Brushes.Gray;
    public Brush Foreground { get; init; } = Brushes.White;
    public Brush SecondaryText { get; init; } = Brushes.LightGray;
    public Brush MutedText { get; init; } = Brushes.Gray;
    public Brush Accent { get; init; } = Brushes.DodgerBlue;
    public Brush StatusLogged { get; init; } = Brushes.LimeGreen;
    public Brush StatusOffline { get; init; } = Brushes.Gray;
    public Brush SplashOverlayBg { get; init; } = Brushes.Black;
    public Brush ErrorBadgeBg { get; init; } = Brushes.DarkRed;
    public Brush ErrorBadgeFg { get; init; } = Brushes.Red;
    public Brush ExternalBarBg { get; init; } = Brushes.Black;
    public Brush ToastBg { get; init; } = Brushes.Black;
    public Brush ToastBorder { get; init; } = Brushes.Gray;
    public System.Drawing.Color WebViewBgColor { get; init; }

    public static ThemePalette Dark { get; } = new()
    {
        IsDark = true,
        WindowBg = Brush(0x0D, 0x0E, 0x11),
        RailBg = Brush(0x0F, 0x11, 0x14),
        DrawerBg = Brush(0x12, 0x14, 0x19),
        SurfaceBg = Brush(0x12, 0x13, 0x16),
        CardBg = Brush(0x15, 0x1A, 0x22),
        InputBg = Brush(0x0F, 0x11, 0x14),
        RowBg = Brush(0x15, 0x17, 0x1C),
        RowActiveBg = Brush(0x1B, 0x27, 0x40),
        TileBg = Brush(0x15, 0x1C, 0x28),
        BorderBrush = Brush(0x23, 0x26, 0x2D),
        ButtonBg = Brush(0x1E, 0x21, 0x27),
        ButtonHoverBg = Brush(0x2A, 0x2E, 0x36),
        Foreground = Brush(0xE9, 0xEB, 0xEE),
        SecondaryText = Brush(0xAE, 0xB4, 0xBD),
        MutedText = Brush(0x8A, 0x90, 0x99),
        Accent = Brush(0x3D, 0x7F, 0xFF),
        StatusLogged = Brush(0x34, 0xC7, 0x59),
        StatusOffline = Brush(0x4A, 0x4F, 0x57),
        SplashOverlayBg = BrushA(0xE6, 0x09, 0x0A, 0x0C),
        ErrorBadgeBg = Brush(0x2A, 0x15, 0x17),
        ErrorBadgeFg = Brush(0xD9, 0x53, 0x5E),
        ExternalBarBg = Brush(0x14, 0x17, 0x1E),
        ToastBg = BrushA(0xF0, 0x1D, 0x1F, 0x24),
        ToastBorder = Brush(0x2A, 0x2E, 0x36),
        WebViewBgColor = System.Drawing.Color.FromArgb(255, 0x12, 0x13, 0x16),
    };

    public static ThemePalette Light { get; } = new()
    {
        IsDark = false,
        WindowBg = Brush(0xF4, 0xF6, 0xF9),
        RailBg = Brush(0xF7, 0xF8, 0xFA),
        DrawerBg = Brush(0xFF, 0xFF, 0xFF),
        SurfaceBg = Brush(0xFF, 0xFF, 0xFF),
        CardBg = Brush(0xF0, 0xF4, 0xFB),
        InputBg = Brush(0xF2, 0xF4, 0xF8),
        RowBg = Brush(0xF5, 0xF7, 0xFA),
        RowActiveBg = Brush(0xE5, 0xEE, 0xFF),
        TileBg = Brush(0xE8, 0xF0, 0xFE),
        BorderBrush = Brush(0xE0, 0xE4, 0xEC),
        ButtonBg = Brush(0xEA, 0xEE, 0xF5),
        ButtonHoverBg = Brush(0xDC, 0xE2, 0xEC),
        Foreground = Brush(0x18, 0x1C, 0x24),
        SecondaryText = Brush(0x4B, 0x55, 0x65),
        MutedText = Brush(0x6E, 0x77, 0x87),
        Accent = Brush(0x2B, 0x6C, 0xF6),
        StatusLogged = Brush(0x22, 0xA8, 0x49),
        StatusOffline = Brush(0xB2, 0xB9, 0xC6),
        SplashOverlayBg = BrushA(0xEB, 0xF6, 0xF8, 0xFB),
        ErrorBadgeBg = Brush(0xFD, 0xEC, 0xEE),
        ErrorBadgeFg = Brush(0xC9, 0x37, 0x45),
        ExternalBarBg = Brush(0xF1, 0xF4, 0xFA),
        ToastBg = BrushA(0xF5, 0x22, 0x26, 0x2F),
        ToastBorder = Brush(0x3B, 0x41, 0x4E),
        WebViewBgColor = System.Drawing.Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
    };

    public static ThemePalette For(bool isDark) => isDark ? Dark : Light;

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush BrushA(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
