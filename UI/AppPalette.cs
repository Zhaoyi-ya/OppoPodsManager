using Avalonia.Media;

namespace OppoPodsManager.UI;

/// <summary>
/// 共享画刷调色板：主窗口（状态/电量）与首页视图（ANC/功能）以及小窗（后续复用）共用的内容画刷。
/// 主题相关画刷按 <see cref="IsLightTheme"/> 在浅色/深色之间切换；外壳在 <c>RefreshThemeColors</c> 中同步该标志。
/// 窗口外观相关画刷（卡片背景、侧栏、对话框遮罩等）仍保留在 MainWindow，不在此处。
/// </summary>
public static class AppPalette
{
    public static bool IsLightTheme { get; set; }

    // 静态（与主题无关）
    public static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    public static readonly SolidColorBrush BrushRed = new(Color.FromRgb(0xFF, 0x55, 0x55));
    public static readonly SolidColorBrush BrushTransparent = new(Colors.Transparent);
    public static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x60, 0x90, 0xFF));
    public static readonly SolidColorBrush BrushWhitePure = new(Colors.White);
    public static readonly SolidColorBrush BrushBatteryLow = new(Color.FromRgb(0xFF, 0x55, 0x55));
    public static readonly SolidColorBrush BrushBatteryMid = new(Color.FromRgb(0xFF, 0xB0, 0x20));
    public static readonly SolidColorBrush BrushBatteryHigh = new(Color.FromRgb(0x4C, 0xD9, 0x64));

    // 主题相关原色
    private static readonly SolidColorBrush _brushGrayDark = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush _brushGrayLight = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush _brushWhiteDark = new(Colors.White);
    private static readonly SolidColorBrush _brushDark = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush _brushLightGreenLight = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush _brushLightGreenDark = new(Color.FromRgb(0x88, 0xCC, 0x88));
    private static readonly SolidColorBrush _brushLightRedLight = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush _brushLightRedDark = new(Color.FromRgb(0xFF, 0x88, 0x88));
    private static readonly SolidColorBrush _brushCircleStrokeLight = new(Color.FromArgb(0x20, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush _brushCircleStrokeDark = new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _brushCircleStrokeInactiveLight = new(Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush _brushCircleStrokeInactiveDark = new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _brushCircleGrayLight = new(Color.FromArgb(0x15, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush _brushCircleGrayDark = new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _brushAccentLight = new(Color.FromRgb(0x25, 0x63, 0xEB));

    // 主题相关访问器
    public static SolidColorBrush BrushGray => IsLightTheme ? _brushGrayLight : _brushGrayDark;
    public static SolidColorBrush BrushWhite => IsLightTheme ? _brushDark : _brushWhiteDark;
    public static SolidColorBrush BrushLightGreen => IsLightTheme ? _brushLightGreenLight : _brushLightGreenDark;
    public static SolidColorBrush BrushLightRed => IsLightTheme ? _brushLightRedLight : _brushLightRedDark;
    public static SolidColorBrush BrushCircleStroke => IsLightTheme ? _brushCircleStrokeLight : _brushCircleStrokeDark;
    public static SolidColorBrush BrushCircleStrokeInactive =>
        IsLightTheme ? _brushCircleStrokeInactiveLight : _brushCircleStrokeInactiveDark;
    public static SolidColorBrush CircleGray => IsLightTheme ? _brushCircleGrayLight : _brushCircleGrayDark;
    public static SolidColorBrush Accent => IsLightTheme ? _brushAccentLight : BrushAccent;
}
