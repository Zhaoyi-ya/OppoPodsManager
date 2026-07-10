using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SukiUI;
using SukiUI.Controls;
using SukiUI.Toasts;

namespace OppoPodsManager;

// 资源路径常量
file static class AppConst
{
    public const string IconConnected = "avares://OppoPodsManager/Assets/tuopan.ico";
    public const string IconDisconnected = "avares://OppoPodsManager/Assets/tuopandis.png";
}

// 从嵌入资源加载图标（适用于 Avalonia + AOT）

// AssetHelper moved to Views/AssetHelper.cs (public, shared with SmallWindow)



public partial class MainWindow : SukiWindow
{
    // 前端只依赖 IPodManager 契约；构造点耦合具体类，其余交互全走接口。
    // 多耳机切换：本字段可被 SwitchToDevice 替换为定向到某副耳机的新实例；
    // 所有事件处理器在触发时实时读取本字段，故替换后自动指向新设备。
    private IPodManager _pods = new PodManager();
    // 当前设备会话令牌：取消它 = 停掉当前耳机的连接循环（切换/关闭时用）。
    private CancellationTokenSource? _sessionCts;
    // 已发现的"当前已连接"耳机 (地址,名称)，与 CbDevice 选项一一对应。
    private readonly List<(ulong addr, string name)> _devices = new();
    // 抑制 CbDevice 程序化改动触发切换。
    private bool _deviceComboSuppress;
    private string _ancMain = "", _ancLevel = "";
    /// <summary>记住每个父模式上次选的子模式（如 降噪→深度），切换回来时恢复。</summary>
    private readonly Dictionary<string, string> _ancLastSub = new();
    private DateTime _ancUserSetAt = DateTime.MinValue;

    // ANC 动态 UI（按 JSON 生成）：键 → (圆形边框, 图标路径, 文字标签)
    private readonly Dictionary<string, (Ellipse bg, Path icon, TextBlock label)> _ancMainButtons = new();
    private readonly Dictionary<string, (Button btn, Border bg)> _ancSubButtons = new();
    private readonly Dictionary<string, string> _ancChildToMain = new();  // 子模式键 → 所属主模式键
    private List<AncOption> _ancOptions = new();                          // 当前型号的 ANC 选项
    private string _ancBuiltForModel = "";                               // 已构建 UI 的型号（避免重复重建）
    private DateTime _featureUserSetAt = DateTime.MinValue;
    private WindowIcon? _iconConnected, _iconDisconnected;
    private TrayIcon? _trayIcon;
    private SmallWindow? _smallWindow;
    private DateTime _smallWindowShownAt = DateTime.MinValue;
    private readonly Util.LogManager _logManager = new();
    private DispatcherTimer? _trayClickTimer;
    private DispatcherTimer? _logRefreshTimer;
    private bool _logAutoScroll = true;
    private ScrollViewer? _logScrollViewer;
    /// <summary>日志显示模式：true=简化版（翻译+合并），false=完整版（原始行）。</summary>
    private bool _logSimplified = true;
    private DispatcherTimer? _eqDebounceTimer;
    private bool _realClose;
    /// <summary>托盘 ANC 菜单项 → (发送键, 父键, 是否子模式)，避免闭包捕获。</summary>
    private readonly Dictionary<NativeMenuItem, (string key, string parentKey, bool isChild)> _trayAncMap = new();
    internal static ISukiToastManager ToastManager = new SukiToastManager();
    private string? _modelOverride;
    private bool _gameModeCompat;
    private bool _wasConnected;
    private bool _lowBatteryAlerted;
    private bool _criticalBatteryAlerted;
    private List<string> _allModelNames = new();

    // 缓存画刷
    private static SolidColorBrush BrushGreen { get; } = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static SolidColorBrush BrushRed { get; } = new(Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush BrushTransparent = new SolidColorBrush(Colors.Transparent);
    // 主题自适应：浅色模式用暗色，深色模式用亮色
    private SolidColorBrush BrushGray => _isLightTheme ? _brushGrayLight : _brushGrayDark;
    private SolidColorBrush BrushWhite => _isLightTheme ? _brushDark : _brushWhiteDark;
    private SolidColorBrush BrushLightGreen => _isLightTheme ? new(Color.FromRgb(0x2E, 0x7D, 0x32)) : new(Color.FromRgb(0x88, 0xCC, 0x88));
    private SolidColorBrush BrushLightRed => _isLightTheme ? new(Color.FromRgb(0xC6, 0x28, 0x28)) : new(Color.FromRgb(0xFF, 0x88, 0x88));
    private SolidColorBrush BrushCircleStroke => _isLightTheme ? new(Color.FromArgb(0x20, 0x00, 0x00, 0x00)) : new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush BrushCircleStrokeInactive => _isLightTheme ? new(Color.FromArgb(0x0C, 0x00, 0x00, 0x00)) : new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _brushGrayDark = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush _brushGrayLight = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush _brushWhiteDark = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush _brushDark = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x60, 0x90, 0xFF));
    private static readonly SolidColorBrush BrushWhitePure = new SolidColorBrush(Colors.White);
    private bool _isLightTheme;

    // ========== ANC 矢量图标 Path Data（从 OPPO 官方 Android App 提取）==========
    private const string IconClose = "M12,1.3C16.253,1.3 19.7,4.747 19.7,9C19.7,10.798 19.083,12.453 18.05,13.764C17.934,13.91 17.876,13.983 17.81,14.027C17.678,14.116 17.51,14.136 17.36,14.081C17.286,14.054 17.212,13.996 17.066,13.881C16.92,13.766 16.848,13.707 16.804,13.642C16.715,13.509 16.694,13.341 16.75,13.19C16.778,13.116 16.835,13.043 16.95,12.898C17.796,11.825 18.3,10.472 18.3,9C18.3,5.521 15.479,2.7 12,2.7C9.764,2.7 7.801,3.866 6.684,5.623L8.373,7.313C9.009,5.947 10.394,5 12,5C14.209,5 16,6.791 16,9C16,10.607 15.052,11.989 13.686,12.625L16.161,15.101C18.31,15.498 20.11,17.047 20.789,19.185L21.041,19.98L23.53,22.47C23.823,22.763 23.823,23.237 23.53,23.53C23.237,23.823 22.763,23.823 22.47,23.53L2.47,3.53C2.177,3.237 2.177,2.763 2.47,2.47C2.763,2.177 3.237,2.177 3.53,2.47L5.673,4.612C7.063,2.611 9.378,1.3 12,1.3ZM19.941,23H3.737C2.87,23 2.245,22.166 2.489,21.334L3.084,19.309C3.834,16.754 6.179,15 8.841,15H11.941L19.941,23ZM5.705,8.764C5.702,8.842 5.7,8.921 5.7,9C5.7,10.472 6.204,11.825 7.05,12.898C7.165,13.043 7.223,13.116 7.25,13.19C7.305,13.341 7.285,13.509 7.196,13.642C7.152,13.707 7.08,13.766 6.934,13.881C6.788,13.996 6.714,14.054 6.64,14.081C6.49,14.136 6.322,14.116 6.189,14.027C6.124,13.983 6.066,13.91 5.95,13.764C4.917,12.453 4.3,10.798 4.3,9C4.3,8.488 4.35,7.988 4.445,7.504L5.705,8.764Z";
    private const string IconNoise = "M15.072,15C17.687,15 20,16.694 20.791,19.185L21.465,21.307C21.731,22.145 21.105,23 20.226,23H3.739C2.872,23 2.247,22.166 2.491,21.334L3.086,19.309C3.836,16.754 6.181,15 8.843,15H15.072ZM12,1.25C16.28,1.25 19.75,4.72 19.75,9C19.75,10.809 19.129,12.476 18.089,13.795C17.945,13.978 17.873,14.069 17.786,14.117C17.67,14.182 17.533,14.199 17.405,14.163C17.31,14.136 17.219,14.064 17.036,13.92C16.853,13.776 16.762,13.704 16.713,13.617C16.648,13.501 16.632,13.363 16.668,13.235C16.695,13.14 16.767,13.049 16.911,12.866C17.75,11.802 18.25,10.461 18.25,9C18.25,5.548 15.452,2.75 12,2.75C8.548,2.75 5.75,5.548 5.75,9C5.75,10.461 6.25,11.802 7.089,12.866C7.233,13.049 7.305,13.14 7.332,13.235C7.368,13.363 7.352,13.501 7.287,13.617C7.238,13.704 7.147,13.776 6.964,13.92C6.781,14.064 6.689,14.136 6.594,14.163C6.466,14.199 6.33,14.182 6.214,14.117C6.127,14.069 6.055,13.978 5.911,13.795C4.871,12.476 4.25,10.809 4.25,9C4.25,4.72 7.72,1.25 12,1.25ZM12,5C14.209,5 16,6.791 16,9C16,11.209 14.209,13 12,13C9.791,13 8,11.209 8,9C8,6.791 9.791,5 12,5Z";
    private const string IconAdaptive = "M15.07,15C17.685,15 19.998,16.693 20.789,19.185L21.463,21.307C21.729,22.145 21.103,23 20.224,23H3.737C2.87,23 2.245,22.165 2.489,21.333L3.084,19.309C3.834,16.754 6.178,15 8.841,15H15.07ZM12,5C14.209,5 16,6.791 16,9C16,11.209 14.209,13 12,13C9.791,13 8,11.209 8,9C8,6.791 9.791,5 12,5ZM20.669,6.22C20.761,5.882 21.239,5.882 21.331,6.22C21.523,6.925 22.075,7.476 22.78,7.668C23.118,7.76 23.118,8.239 22.78,8.331C22.075,8.523 21.523,9.074 21.331,9.779C21.239,10.117 20.761,10.117 20.669,9.779C20.477,9.074 19.926,8.523 19.221,8.331C18.883,8.239 18.883,7.76 19.221,7.668C19.926,7.476 20.477,6.925 20.669,6.22ZM17.448,0.533C17.601,-0.029 18.4,-0.029 18.553,0.533C18.872,1.709 19.79,2.628 20.966,2.947C21.529,3.1 21.529,3.899 20.966,4.052C19.79,4.371 18.872,5.29 18.553,6.466C18.4,7.029 17.601,7.029 17.448,6.466C17.129,5.29 16.21,4.371 15.034,4.052C14.471,3.899 14.471,3.1 15.034,2.947C16.21,2.628 17.129,1.709 17.448,0.533Z";
    private const string IconTransparency = "M15.07,15C17.685,15 19.998,16.694 20.789,19.185L21.463,21.307C21.729,22.145 21.103,23 20.224,23H3.737C2.87,23 2.245,22.166 2.489,21.334L3.084,19.309C3.834,16.754 6.179,15 8.841,15H15.07ZM12,5C14.209,5 16,6.791 16,9C16,11.209 14.209,13 12,13C9.791,13 8,11.209 8,9C8,6.791 9.791,5 12,5ZM5.15,10.755C5.661,10.544 6.246,10.786 6.457,11.296C6.668,11.806 6.425,12.391 5.915,12.602C5.405,12.814 4.821,12.571 4.609,12.061C4.398,11.551 4.64,10.966 5.15,10.755ZM17.543,11.296C17.754,10.786 18.339,10.544 18.85,10.755C19.36,10.966 19.602,11.551 19.391,12.061C19.179,12.572 18.594,12.814 18.084,12.602C17.574,12.391 17.332,11.806 17.543,11.296ZM5,8C5.552,8 6,8.448 6,9C6,9.552 5.552,10 5,10C4.448,10 4,9.552 4,9C4,8.448 4.448,8 5,8ZM19,8C19.552,8 20,8.448 20,9C20,9.552 19.552,10 19,10C18.448,10 18,9.552 18,9C18,8.448 18.448,8 19,8ZM4.609,5.938C4.821,5.428 5.405,5.186 5.915,5.397C6.425,5.609 6.668,6.194 6.457,6.704C6.246,7.214 5.661,7.456 5.15,7.245C4.64,7.034 4.398,6.449 4.609,5.938ZM18.084,5.397C18.594,5.186 19.179,5.428 19.391,5.938C19.602,6.449 19.36,7.034 18.85,7.245C18.339,7.456 17.754,7.214 17.543,6.704C17.332,6.194 17.574,5.609 18.084,5.397ZM6.343,3.343C6.733,2.952 7.366,2.953 7.757,3.343C8.147,3.733 8.147,4.367 7.757,4.758C7.366,5.148 6.733,5.148 6.343,4.758C5.952,4.367 5.952,3.733 6.343,3.343ZM16.242,3.343C16.633,2.952 17.267,2.952 17.657,3.343C18.048,3.733 18.048,4.367 17.657,4.758C17.267,5.148 16.633,5.148 16.242,4.758C15.852,4.367 15.852,3.733 16.242,3.343ZM8.938,1.609C9.449,1.398 10.034,1.64 10.245,2.15C10.456,2.661 10.214,3.246 9.704,3.457C9.194,3.668 8.609,3.425 8.397,2.915C8.186,2.405 8.428,1.821 8.938,1.609ZM13.755,2.15C13.966,1.64 14.551,1.398 15.061,1.609C15.571,1.821 15.814,2.405 15.602,2.915C15.391,3.425 14.806,3.668 14.296,3.457C13.786,3.246 13.544,2.661 13.755,2.15ZM12,1C12.552,1 13,1.448 13,2C13,2.552 12.552,3 12,3C11.448,3 11,2.552 11,2C11,1.448 11.448,1 12,1Z";

    /// <summary>按模式键取对应图标 path。</summary>
    private static string GetAncIcon(string key) => key switch
    {
        "Off" => IconClose,
        "Transparency" => IconTransparency,
        "Adaptive" => IconAdaptive,
        _ => IconNoise   // NC / Smart / Light / Medium / Deep 等降噪子模式共用降噪图标
    };

    // Feather Icons：设置(齿轮) & 信息
    private const string IconFeatherSettings = "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z";
    private const string IconFeatherInfo = "M12 22C6.477 22 2 17.523 2 12S6.477 2 12 2s10 4.477 10 10-4.477 10-10 10zm0-2a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM11 7h2v2h-2V7zm0 4h2v6h-2v-6z";

    // CheckBox 脏检查状态
    private bool _prevSpatialSound;
    private bool _prevGameMode;
    private bool _prevGameSound;
    private bool _prevDualDevice;
    private string _prevSpatialMode = "";

    // 三级联动：品牌 → 子系列 → 机型
    private readonly ObservableCollection<string> _brandList = new();
    private readonly ObservableCollection<string> _seriesList = new();
    private readonly ObservableCollection<string> _modelList = new();
    private Dictionary<string, Dictionary<string, List<string>>> _brandTree = new();

    public MainWindow()
    {
        try
        {
            Log.D("UI", "MainWindow 构造开始");
        InitializeComponent();
        NavHome.Classes.Add("selected");

        // 挂载调试日志监听器，捕获 Log.D/Ex 输出到 UI 日志面板
        _logManager.CleanOldLogs(7); // 启动时清理 7 天前的旧日志
        Trace.Listeners.Add(new LogTraceListener(_logManager));
            Log.D("UI", "InitializeComponent OK");

        // Wire events programmatically (Avalonia 12 compatibility)
            CbSpatial.IsCheckedChanged += CbSpatial_Changed;
            CbGame.IsCheckedChanged += CbGame_Changed;
            CbGameSound.IsCheckedChanged += CbGameSound_Changed;
        CbDualDevice.IsCheckedChanged += CbDualDevice_Changed;
        CbTray.IsCheckedChanged += CbTray_Changed;
        CbAuto.IsCheckedChanged += CbAuto_Changed;
        CbAutoUpdate.IsCheckedChanged += CbAutoUpdate_Changed;
        CbEq.SelectionChanged += CbEq_SelectionChanged;
        CbBrand.SelectionChanged += CbBrand_Changed;
        CbSeries.SelectionChanged += CbSeries_Changed;
        CbModel.SelectionChanged += CbModel_Changed;
        CbGameMode.SelectionChanged += CbGameMode_Changed;
        CbTheme.SelectionChanged += CbTheme_Changed;
        TbCustomName.TextChanged += TbCustomName_Changed;

        BuildEqBandControls(GetCurrentEqFrequencies());
        // EQ 预设列表（左右分栏：系统预设 / 自定义）
        LbEqBuiltinPresets.SelectionChanged += EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged += EqCustomPresets_Changed;

        // 初始加载自定义 EQ 预设列表
        RefreshEqPresetList();

        // 多设备下拉
        // 多设备下拉
        SyncMultiDeviceList();

        // 主题：默认跟随系统
        var themeIndex = SettingsManager.GetInt("Theme", 0);
        ApplyTheme(themeIndex);
        CbTheme.SelectedIndex = themeIndex;

        // 开机自启静默启动：不显示窗口、不在任务栏出现（仅托盘图标）。
        // App.OnFrameworkInitializationCompleted 在 --minimized 时不会调用 Show()，
        // 这里再关掉任务栏显示，确保开机时任务栏不闪现任务。
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized"))
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }

        // 固定在屏幕右下角（已取消，使用默认居中）

        // 托盘图标
        _iconConnected = AssetHelper.LoadIcon(AppConst.IconConnected);
        _iconDisconnected = AssetHelper.LoadIcon(AppConst.IconDisconnected);
        SetupTrayIcon();
        // Setup SukiUI toast host
        Hosts = [new SukiToastHost { Manager = ToastManager }];

        // Load battery: official product image + icon paths
        var caseBmp = AssetHelper.LoadBitmap("avares://OppoPodsManager/Assets/official_case.png");
        if (caseBmp != null) BatteryImage.Source = caseBmp;

        // Battery icon paths (from OPPO official app, same as SmallWindow)
        const string iconCase = "M7.976,1.523H7.992H11.039H11.055H11.056C11.394,1.523 11.58,1.523 11.739,1.532C14.304,1.666 16.444,3.377 17.212,5.716H16.795C16.795,5.716 16.795,5.716 16.795,5.716H13.267C13.165,5.279 12.772,4.954 12.303,4.954H6.665C6.197,4.954 5.804,5.279 5.701,5.716H2.208C2.208,5.716 2.208,5.716 2.208,5.716H1.819C2.587,3.377 4.727,1.666 7.292,1.532C7.451,1.523 7.637,1.523 7.976,1.523H7.976Z M16.676,6.706H17.447C17.477,6.901 17.497,7.099 17.507,7.3C17.516,7.459 17.516,7.645 17.516,7.984V8V8.015C17.516,8.354 17.516,8.54 17.507,8.7C17.344,11.815 14.855,14.304 11.739,14.467C11.58,14.476 11.394,14.476 11.055,14.476H11.039H7.992H7.976C7.637,14.476 7.451,14.476 7.292,14.467C4.176,14.304 1.687,11.815 1.524,8.7C1.516,8.54 1.516,8.354 1.516,8.016V8.015V8V7.984V7.984C1.516,7.645 1.516,7.459 1.524,7.3C1.534,7.099 1.555,6.901 1.584,6.706H2.356C2.356,6.706 2.356,6.707 2.356,6.707H5.787C5.952,7.023 6.283,7.24 6.665,7.24H12.303C12.685,7.24 13.017,7.023 13.182,6.707H16.676C16.676,6.707 16.676,6.706 16.676,6.706Z M9.501,10.287C9.922,10.287 10.263,9.946 10.263,9.525C10.263,9.104 9.922,8.763 9.501,8.763C9.081,8.763 8.74,9.104 8.74,9.525C8.74,9.946 9.081,10.287 9.501,10.287Z";
        const string iconL = "M3.963,9.543H8.604V8.337H5.458V2.461H3.963V9.543Z";
        const string iconR = "M3.992,2.871V11.133H5.726V8.026H6.907L8.934,11.133H11.016L8.708,7.79C9.219,7.602 9.613,7.306 9.89,6.901C10.168,6.488 10.307,6.004 10.307,5.449C10.307,4.931 10.187,4.481 9.947,4.098C9.714,3.708 9.369,3.408 8.911,3.198C8.461,2.98 7.924,2.871 7.301,2.871H3.992Z M8.472,5.449C8.472,6.282 7.969,6.698 6.964,6.698H5.726V4.199H6.964C7.969,4.199 8.472,4.616 8.472,5.449Z";
        const string iconCharge = "M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z";
        IconCase.Data = StreamGeometry.Parse(iconCase);
        IconLeftCircle.Data = StreamGeometry.Parse("M6,12C9.314,12 12,9.314 12,6C12,2.686 9.314,0 6,0C2.686,0 0,2.686 0,6C0,9.314 2.686,12 6,12Z");
        IconLeftLetter.Data = StreamGeometry.Parse(iconL);
        IconRightCircle.Data = StreamGeometry.Parse("M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14Z");
        IconRightLetter.Data = StreamGeometry.Parse(iconR);
        IconCharge.Data = StreamGeometry.Parse(iconCharge);
        // 先填充默认 EQ 列表
        foreach (var kv in _pods.Caps.EqPresets) CbEq.Items.Add(kv.Key);
        CbTray.IsChecked = SettingsManager.GetBool("TrayEnabled", false);
        CbAuto.IsChecked = SettingsManager.GetBool("AutoStart", false);
        // 用 SetString/GetString 避免 SetBool(false) 删除条目导致默认值恢复
        var autoUpdate = SettingsManager.GetString("AutoCheckUpdate");
        CbAutoUpdate.IsChecked = autoUpdate != "false"; // 首次 null → true

        // 弹窗时长
        var durIdx = SettingsManager.GetInt("ToastDuration", 2);
        CbToastDuration.SelectedIndex = durIdx >= 0 && durIdx <= 5 ? durIdx : 2;
        CbToastDuration.SelectionChanged += (_, _) =>
            SettingsManager.SetInt("ToastDuration", CbToastDuration.SelectedIndex);

        // 设备型号选择
        _allModelNames = DeviceCapabilities.GetModelNames();
        _brandTree = BuildBrandTree(_allModelNames);

        CbBrand.ItemsSource = _brandList;
        CbSeries.ItemsSource = _seriesList;
        CbModel.ItemsSource = _modelList;

        _brandList.Add("自动检测");
        foreach (var brand in _brandTree.Keys.OrderBy(b => b)) _brandList.Add(brand);

        _modelOverride = SettingsManager.GetString("ModelOverride");
        if (string.IsNullOrEmpty(_modelOverride))
        {
            CbBrand.SelectedItem = "自动检测";
        }
        else
        {
            var (brand, series) = FindBrandSeries(_modelOverride, _brandTree);
            CbBrand.SelectedItem = brand ?? "自动检测";
            if (brand != null)
            {
                _seriesList.Clear();
                _seriesList.Add("（全部子系列）");
                foreach (var s in _brandTree[brand].Keys.OrderBy(s => s)) _seriesList.Add(s);
                CbSeries.SelectedItem = series ?? "（全部子系列）";
                _modelList.Clear();
                _modelList.Add("（全部机型）");
                foreach (var m in _brandTree[brand][series ?? _brandTree[brand].Keys.First()].OrderBy(m => m))
                    _modelList.Add(m);
                CbModel.SelectedItem = _modelOverride;
            }
        }

        _gameModeCompat = SettingsManager.GetBool("GameModeCompat", false);
        CbGameMode.SelectedIndex = _gameModeCompat ? 1 : 0;

        var customName = SettingsManager.GetString("CustomName");
        TbCustomName.Text = customName ?? "";
        UpdateTitle();

        // 多耳机选择器：发现在后台线程做，避免阻塞构造/UI
        CbDevice.SelectionChanged += CbDevice_Changed;

        WirePods(_pods);
        Closing += OnWindowClosing;
        Closed += (_, _) => { _sessionCts?.Cancel(); _pods.Dispose(); _logManager.Dispose(); };
        _ = InitAndStartAsync();      // 异步发现已连接耳机 → 定向 → 启动连接循环
        _ = CheckForUpdateAsync();    // 后台检查更新
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "MainWindow 构造", ex);
            throw;
        }
    }

    /// <summary>
    /// 启动流程：后台发现"当前已连接"的耳机 → 若有则定向到（上次保存/第一个）→ 启动连接循环。
    /// 发现为空时保留默认实例（连第一个匹配），连接成功后会自动补一次发现填充选择器。
    /// </summary>
    private async Task InitAndStartAsync()
    {
        try
        {
            var devices = await Task.Run(() => DeviceDiscovery.ListConnected());
            ApplyDeviceList(devices);

            if (_devices.Count > 0)
            {
                ulong saved = ParseAddr(SettingsManager.GetString("LastDeviceAddr"));
                int idx = saved != 0 ? _devices.FindIndex(d => d.addr == saved) : -1;
                if (idx < 0) idx = 0;

                var (addr, name) = _devices[idx];
                _deviceComboSuppress = true;
                CbDevice.SelectedIndex = idx;
                _deviceComboSuppress = false;

                var old = _pods;
                UnwirePods(old);
                _pods = new PodManager(addr, name);
                WirePods(_pods);
                try { old.Dispose(); } catch { }
                Log.D("UI", $"InitAndStart: 定向连接 addr={addr:X12} name=\"{name}\"");
            }
            else
            {
                Log.D("UI", "InitAndStart: 当前无已连接耳机,沿用默认(第一个匹配)连接");
            }
        }
        catch (Exception ex) { Log.Ex("UI", "InitAndStartAsync", ex); }

        StartSession();
    }

    /// <summary>为当前 _pods 启动一个连接循环会话（带独立会话令牌）。</summary>
    private void StartSession()
    {
        _sessionCts = new CancellationTokenSource();
        _ = ConnectLoop(_pods, _sessionCts.Token);
    }

    /// <summary>
    /// 单副耳机的连接/重连循环。绑定到传入的 pods 实例与会话令牌：
    /// - 会话令牌取消（切换设备/关闭）→ 退出循环并释放该实例（本循环独占其生命周期）。
    /// - Reconnect/Disconnect 只停 PollAsync，不取消会话，故循环会自动重连。
    /// - UI 更新前用 ReferenceEquals 校验，避免慢速旧连接覆盖新设备界面。
    /// </summary>
    private async Task ConnectLoop(IPodManager pods, CancellationToken sessionCt)
    {
        try
        {
            while (!_realClose && !sessionCt.IsCancellationRequested)
            {
                Log.D("UI", "ConnectLoop: 尝试连接");
                await pods.ConnectAsync();
                if (sessionCt.IsCancellationRequested) break;

                if (pods.IsConnected)
                {
                    Log.D("UI", "ConnectLoop: 已连接,进入轮询");
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
                    // Reconnect/Disconnect 通过 pods.Disconnect() 令 PollAsync 自然结束
                    await pods.PollAsync(pollCts.Token);
                    Log.D("UI", "ConnectLoop: 轮询结束");
                }
                else
                {
                    Log.D("UI", "ConnectLoop: 连接失败 -> " + (pods.LastError ?? "unknown"));
                }

                if (ReferenceEquals(pods, _pods))
                    _ = Dispatcher.UIThread.InvokeAsync(() => OnStateChanged());

                if (!_realClose && !sessionCt.IsCancellationRequested)
                {
                    Log.D("UI", "ConnectLoop: 5s 后重试");
                    try { await Task.Delay(5000, sessionCt); } catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            Log.D("UI", "ConnectLoop: 会话结束,释放该设备实例");
            try { pods.Dispose(); } catch { }
        }
    }

    // ==================== 多耳机选择器 ====================
    // 只列"当前已连接"的耳机；发现动作仅在 3 个时机触发，绝不轮询：
    //   1) 启动时（InitAndStartAsync）
    //   2) 本机连接成功后一次（补齐列表，见 OnStateChanged 首连分支）
    //   3) 用户点刷新按钮
    private bool _deviceListRefreshedOnConnect;

    /// <summary>把发现结果套用到 _devices + CbDevice 选项（须在 UI 线程调用）。保持不误触发切换。</summary>
    private void ApplyDeviceList(IReadOnlyList<(ulong addr, string name)> devices)
    {
        // 记住当前选中地址，套用后尽量恢复
        ulong current = _devices.Count > 0 && CbDevice.SelectedIndex >= 0 && CbDevice.SelectedIndex < _devices.Count
            ? _devices[CbDevice.SelectedIndex].addr
            : ParseAddr(SettingsManager.GetString("LastDeviceAddr"));

        _devices.Clear();
        _devices.AddRange(devices);

        _deviceComboSuppress = true;
        CbDevice.Items.Clear();
        foreach (var (addr, name) in _devices) CbDevice.Items.Add(name);
        int restore = current != 0 ? _devices.FindIndex(d => d.addr == current) : -1;
        if (restore >= 0) CbDevice.SelectedIndex = restore;
        _deviceComboSuppress = false;

        // 选择器仅在有 2 副及以上已连接耳机时显示（单副无需切换）；刷新按钮常显以便重扫
        CbDevice.IsVisible = _devices.Count >= 2;
        BtnRefreshDevices.IsVisible = true;
    }

    /// <summary>后台重新发现已连接耳机并刷新选择器（供刷新按钮/首连补齐用）。</summary>
    private async Task RefreshDeviceListAsync()
    {
        var devices = await Task.Run(() => DeviceDiscovery.ListConnected());
        ApplyDeviceList(devices);
    }

    /// <summary>用户在选择器里切换耳机。</summary>
    private void CbDevice_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_deviceComboSuppress) return;
        int idx = CbDevice.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count) return;

        var (addr, name) = _devices[idx];
        // 与当前正连着的同一台则忽略
        if (_pods.IsConnected && ParseAddr(SettingsManager.GetString("LastDeviceAddr")) == addr)
            return;

        Log.D("UI", $"用户操作: 切换耳机 -> addr={addr:X12} name=\"{name}\"");
        SwitchToDevice(addr, name);
    }

    /// <summary>刷新按钮：后台重新扫描"当前已连接"的耳机。</summary>
    private async void RefreshDevices_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 刷新耳机列表");
        try { await RefreshDeviceListAsync(); }
        catch (Exception ex) { Log.Ex("UI", "RefreshDevices_Click", ex); }
    }

    /// <summary>
    /// 切换到指定耳机：停当前会话(异步释放旧实例)→ 换 _pods → 重连。
    /// 关闭可能过期的小窗（它持有旧 _pods 引用），下次打开会用新实例重建。
    /// </summary>
    private void SwitchToDevice(ulong addr, string? name)
    {
        // 1) 停当前会话（旧循环在 ConnectAsync 返回后会自行退出并释放旧实例）
        _sessionCts?.Cancel();
        _pods.Disconnect();          // 尽快让 PollAsync 结束

        // 2) 解绑旧实例事件，关闭过期小窗
        UnwirePods(_pods);
        if (_smallWindow != null)
        {
            try { _smallWindow.Close(); } catch { }
            _smallWindow = null;
        }

        // 3) 换上定向到新耳机的实例
        _pods = new PodManager(addr, name);
        WirePods(_pods);
        _deviceListRefreshedOnConnect = false;   // 新设备连上后允许再补一次发现
        SettingsManager.SetString("LastDeviceAddr", addr.ToString("X12"));

        // 4) UI 立即反映"连接中"，并启动新会话
        ResetUi();
        OnStateChanged();
        StartSession();
    }

    /// <summary>订阅 pods 的状态事件（合并为单一方法便于切换时解绑）。</summary>
    private void WirePods(IPodManager pods) => pods.StateChanged += PodsStateChanged;
    private void UnwirePods(IPodManager pods) => pods.StateChanged -= PodsStateChanged;

    /// <summary>pods 状态变化统一处理：刷新主界面 + 同步多设备列表。</summary>
    private void PodsStateChanged()
    {
        OnStateChanged();
        Dispatcher.UIThread.Post(() => SyncMultiDeviceList());
    }

    /// <summary>解析 12 位十六进制地址字符串为 48 位地址；失败返回 0。</summary>
    private static ulong ParseAddr(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var a) ? a : 0;
    }

    private void OnStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            var s = _pods.State;
            var caps = _modelOverride != null
                ? DeviceCapabilities.ForceModel(_modelOverride)
                : _pods.Caps;

            if (s.Connected)
        {
            StatusDot.Fill = BrushGreen;
            StatusText.Text = caps.IsSupported
                ? $"已连接 — {caps.ModelName}"
                : $"已连接 — {caps.ModelName}（此型号可能未完整适配）";
            StatusText.Foreground = BrushLightGreen;
            BtnReconnect.IsVisible = false;

            if (caps.HasDualDevice)
            {
                SyncMultiDeviceList();
            }

            if (!_wasConnected && s.Battery.Count > 0)
            {
                _wasConnected = true;

                // 检查是否低电/极低电，跳过普通弹窗直出对应类型
                var anyCritical = s.Battery.Values.Any(v => v?.Level <= 10);
                var anyLow = s.Battery.Values.Any(v => v?.Level <= 20 && v?.Level > 10);
                if (!anyCritical && !anyLow)
                {
                    _ = ToastWindow.ShowAsync(s, GetDeviceDisplayName(), ToastType.Battery, GetToastDuration());
                }

                _pods.SendQueryEqAll();  // 首次连接时查询设备端 EQ 列表

                // 首次连接成功后补一次"已连接耳机"发现（事件驱动，非轮询），填充选择器。
                // 每个会话只补一次，避免重连反复扫描。
                if (!_deviceListRefreshedOnConnect)
                {
                    _deviceListRefreshedOnConnect = true;
                    _ = RefreshDeviceListAsync();
                }
            }
        }
        else
        {
            var wasConnected = _wasConnected;
            _wasConnected = false;
            _lowBatteryAlerted = false;
            _criticalBatteryAlerted = false;

            StatusDot.Fill = BrushRed;
            var err = _pods.LastError;
            StatusText.Text = err ?? "未连接";
            StatusText.Foreground = BrushLightRed;
            StatusDot.IsVisible = true;
            StatusText.IsVisible = true;
            BtnReconnect.IsVisible = true;
            
            // TrayIcon.SetIcon(this, _iconDisconnected); // 托盘图标切换在 SetupTrayIcon 中处理

            if (wasConnected)
                _ = ToastWindow.ShowAsync(null, GetDeviceDisplayName(), ToastType.Disconnected, GetToastDuration());

            ResetUi();
            RebuildTrayMenu();
            return;
        }

        // 检测是否需要刷新 EQ 列表（连接后预设可能变化）
        // 检测 EQ 列表是否需要刷新（设备端条目增减时）
        var prevCount = _pods.State.DeviceEqEntries.Count;
        if (CbEq.ItemCount == 0 || prevCount != _lastDeviceEqCount
            || caps.EqPresets.Keys.Any(k => !CbEq.Items.Contains(k))
            || _pods.State.DeviceEqEntries.Any(e => !string.IsNullOrWhiteSpace(e.Name) && !CbEq.Items.Contains(e.Name)))
        {
            RefreshAllEqViews();
        }
        _lastDeviceEqCount = prevCount;

        var batL = MergeCharge(s.Battery.GetValueOrDefault("L"), s.WearingL);
        var batR = MergeCharge(s.Battery.GetValueOrDefault("R"), s.WearingR);
        SetBatLabel(LeftLabel, batL);
        SetBatLabel(RightLabel, batR);
        SetBatLabel(CaseLabel, s.Battery.GetValueOrDefault("C"));
        ChargeIndicator.IsVisible = s.Battery.Values.Any(v => v?.Charging == true);

        if (!_criticalBatteryAlerted)
        {
            if ((batL is { } lc && lc.Lvl <= 10) || (batR is { } rc && rc.Lvl <= 10))
            {
                _criticalBatteryAlerted = true;
                _lowBatteryAlerted = true; // 极低电量跳过低电量弹窗
                _ = ToastWindow.ShowAsync(s, GetDeviceDisplayName(), ToastType.CriticalBattery, GetToastDuration());
            }
        }

        if (!_lowBatteryAlerted)
        {
            if ((batL is { } l && l.Lvl <= 20 && l.Lvl > 10) || (batR is { } r && r.Lvl <= 20 && r.Lvl > 10))
            {
                _lowBatteryAlerted = true;
                _ = ToastWindow.ShowAsync(s, GetDeviceDisplayName(), ToastType.LowBattery, GetToastDuration());
            }
        }

        var parts = new List<string>();
        if (batL is { } lb) parts.Add($"L:{lb.Lvl}%{(lb.Chg ? "(充)" : "")}");
        if (batR is { } rb) parts.Add($"R:{rb.Lvl}%{(rb.Chg ? "(充)" : "")}");
        if (s.Battery.GetValueOrDefault("C") is { } cb) parts.Add($"C:{cb.Level}%{(cb.Charging ? "(充)" : "")}");
        UpdateTrayTooltip(parts.Count > 0 ? $"{caps.ModelName}\n{string.Join(" ", parts)}" : caps.ModelName);

        // 佩戴状态 - 即使空也显示占位，排查显示问题
        var wearParts = new List<string>();
        if (!string.IsNullOrEmpty(s.WearingL)) wearParts.Add($"左耳{s.WearingL}");
        if (!string.IsNullOrEmpty(s.WearingR)) wearParts.Add($"右耳{s.WearingR}");
        WearStatus.Text = wearParts.Count > 0 ? string.Join("  ", wearParts) : "";
        WearStatus.IsVisible = wearParts.Count > 0;

        // 查找设备：两只耳机均未佩戴时才可用
        var anyWearing = s.WearingL == "已佩戴" || s.WearingR == "已佩戴"
                      || s.WearingL == "佩戴"   || s.WearingR == "佩戴";
        BtnFindDevice.IsEnabled = caps.HasFindDevice && s.Connected && !anyWearing;

        if (s.AncMode is not "?" && (DateTime.Now - _ancUserSetAt).TotalSeconds > 3)
            SyncAncFromState(s.AncMode);
        HighlightAnc();

        // 智能切换：显示设备实时计算出的档位（如"实时计算：深度"）。
        // Smart 在容器型设备是子档位(_ancLevel)，在扁平型是主模式(_ancMain)，两者都要判。
        if ((_ancMain == "Smart" || _ancLevel == "Smart") && !string.IsNullOrEmpty(s.IntelligentRealtime))
        {
            AncRealtimeHint.Text = $"实时计算：{AncModeLabel(s.IntelligentRealtime)}";
            AncRealtimeHint.IsVisible = true;
        }
        else
        {
            AncRealtimeHint.IsVisible = false;
        }

        if (s.EqPreset != "?" && CbEq.SelectedItem == null)
        {
            _eqCurrentPreset = s.EqPreset;
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.SelectedItem = s.EqPreset;
            CbEq.SelectionChanged += CbEq_SelectionChanged;
        }
        if ((DateTime.Now - _featureUserSetAt).TotalSeconds > 3)
        {
            // 状态回读同步：均用静默设置，避免初始化/轮询勾选反向触发 _Changed 下发命令
            if (s.SpatialSound != _prevSpatialSound) { _prevSpatialSound = s.SpatialSound; SetSpatialCheckedSilent(s.SpatialSound); CbGameSound.IsEnabled = !s.SpatialSound; }
            if (caps.HasSpatialAudio && s.SpatialMode != _prevSpatialMode) { _prevSpatialMode = s.SpatialMode; SyncSpatialModeFromState(s.SpatialMode); }
            if (s.GameMode != _prevGameMode) { _prevGameMode = s.GameMode; SetGameCheckedSilent(s.GameMode); }
            if (s.GameSound != _prevGameSound) { _prevGameSound = s.GameSound; SetGameSoundCheckedSilent(s.GameSound); CbSpatial.IsEnabled = !s.GameSound; SetEqControlsEnabled(!s.GameSound); }
            if (s.DualDevice != _prevDualDevice) { _prevDualDevice = s.DualDevice; SetDualDeviceCheckedSilent(s.DualDevice); }
        }

        if (DeviceInfoPanel.IsVisible) RefreshDeviceInfo();

        BuildAncUi(caps);
        SpatialAudioPanel.IsVisible = caps.HasSpatialAudio;
        // CbSpatial.IsVisible = caps.HasSpatialSound;
        // CbDualDevice.IsVisible = caps.HasDualDevice;
        // CbGame.IsVisible = caps.HasGameMode;
        // CbGameSound.IsVisible = caps.HasGameSound;

        // 全部占位控件暂不按能力隐藏，全量显示便于验证布局
        CbSpatial.IsVisible = caps.HasSpatialSound;
        CbDualDevice.IsVisible = caps.HasDualDevice;
        CbGame.IsVisible = caps.HasGameMode;
        CbGameSound.IsVisible = caps.HasGameSound;

        // 以下功能后端未实现，暂时隐藏
        // CbBassEngine.IsVisible = caps.HasBassEngine;
        // CbVocalEnhance.IsVisible = caps.HasVocalEnhance;
        // CbHearingEnhance.IsVisible = caps.HasHearingEnhancement;
        // CbLongPower.IsVisible = caps.HasLongPowerMode;
        // CbWearDetection.IsVisible = caps.HasWearDetection;
        // CbSpineHealth.IsVisible = caps.HasSpineHealth;
        // BtnFindDevice.IsVisible = caps.HasFindDevice;

        ModelNote.Text = $"当前自动识别: {caps.ModelName}";
        UpdateTitle();
        RebuildTrayMenu();
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "OnStateChanged", ex);
        }
    });

    private static void SetBatLabel(TextBlock l, (int Lvl, bool Chg)? d)
    {
        if (d is not { } v) { l.Text = "-%"; return; }
        l.Text = $"{v.Lvl}%{(v.Chg ? " (充)" : "")}";
    }

    private static (int Lvl, bool Chg)? MergeCharge((int Lvl, bool Chg)? bat, string wear) =>
        bat is { } b ? (b.Lvl, b.Chg || wear == "入盒") : null;

    private void CbTray_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsManager.SetBool("TrayEnabled", CbTray.IsChecked == true);
    }

    private void CbAuto_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool enable = CbAuto.IsChecked == true;
        SettingsManager.SetBool("AutoStart", enable);
        try { ApplyAutoStart(enable); }
        catch (Exception ex) { Log.Ex("UI", "ApplyAutoStart", ex); }
    }

    /// <summary>
    /// 按平台注册/取消开机自启（都带 --minimized 静默启动）：
    ///   Windows：写 HKCU\...\Run 注册表值；
    ///   Linux：写 ~/.config/autostart/oppopods.desktop（XDG 自启规范，GNOME/KDE/XFCE 通用）。
    /// 其它平台静默跳过。
    /// </summary>
    private static void ApplyAutoStart(bool enable)
    {
        var exe = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsWindows())
        {
            using var runKey = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey is null) return;
            if (enable) runKey.SetValue("OPPOPods", $"\"{exe}\" --minimized");
            else { try { runKey.DeleteValue("OPPOPods", throwOnMissingValue: false); } catch { } }
        }
        else if (OperatingSystem.IsLinux())
        {
            // 全限定 System.IO 以避免与 Avalonia.Controls.Shapes.Path 冲突
            var autostartDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
            var desktopFile = System.IO.Path.Combine(autostartDir, "oppopods.desktop");
            if (enable)
            {
                System.IO.Directory.CreateDirectory(autostartDir);
                // Exec 用 --minimized 静默启动；X-GNOME-Autostart-enabled 供部分 DE 识别。
                var content =
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=OPPO Pods Manager\n" +
                    $"Exec=\"{exe}\" --minimized\n" +
                    "Terminal=false\n" +
                    "X-GNOME-Autostart-enabled=true\n";
                System.IO.File.WriteAllText(desktopFile, content);
                Log.D("UI", $"ApplyAutoStart: 已写入 {desktopFile}");
            }
            else
            {
                try { if (System.IO.File.Exists(desktopFile)) System.IO.File.Delete(desktopFile); } catch { }
                Log.D("UI", "ApplyAutoStart: 已移除 Linux 自启");
            }
        }
    }
    private void CbAutoUpdate_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        => SettingsManager.SetString("AutoCheckUpdate", CbAutoUpdate.IsChecked == true ? "true" : "false");

    /// <summary>按当前型号 caps.AncOptions 动态生成主/子模式圆形图标按钮（型号不变则跳过重建）。</summary>
    private void BuildAncUi(DeviceCapabilities caps)
    {
        var modelKey = caps.ModelId + "|" + caps.ModelName;
        if (_ancBuiltForModel == modelKey && caps.AncOptions.Count > 0) return;
        _ancBuiltForModel = modelKey;
        _ancOptions = caps.AncOptions;

        _ancMainButtons.Clear();
        _ancChildToMain.Clear();
        AncMainRow.Children.Clear();
        AncMainRow.ColumnDefinitions.Clear();
        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        _ancSubButtons.Clear();
        AncSubRow.IsVisible = false;

        // 无 ANC 选项或未连接 → 显示占位文字，隐藏按钮行
        if (_ancOptions.Count == 0)
        {
            AncPlaceholderText.IsVisible = true;
            AncMainRow.IsVisible = false;
            return;
        }
        AncPlaceholderText.IsVisible = false;
        AncMainRow.IsVisible = true;

        int col = 0;
        for (int i = 0; i < _ancOptions.Count; i++)
        {
            var opt = _ancOptions[i];
            if (i > 0) AddSpacer(AncMainRow, ref col, 16);
            var (panel, bg, stroke, icon, label) = MakeAncIconButton(opt, 56, 28, 11, AncMain_Click);
            AddToRow(AncMainRow, panel, ref col);
            _ancMainButtons[opt.Key] = (bg, icon, label);

            foreach (var child in opt.Children)
                _ancChildToMain[child.Key] = opt.Key;
        }
        HighlightAnc();
    }

    /// <summary>填充子模式行（纯文字按钮，不带图标）。</summary>
    private void PopulateAncSub(AncOption container)
    {
        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        _ancSubButtons.Clear();

        int col = 0;
        for (int i = 0; i < container.Children.Count; i++)
        {
            var child = container.Children[i];
            if (i > 0) AddSeparator(AncSubRow, ref col);
            var corner = FirstLast(i, container.Children.Count, 5);
            var (btn, bg) = MakeTextButton(child.Label, child, 72, 28, 13, corner, AncSub_Click);
            AddToRow(AncSubRow, bg, ref col);
            _ancSubButtons[child.Key] = (btn, bg);
        }
    }

    /// <summary>创建图标+文字按钮：Ellipse 圆形背景+描边 + 矢量图标 + 文字。</summary>
    private (Control panel, Ellipse bg, Ellipse stroke, Path icon, TextBlock label) MakeAncIconButton(
        AncOption opt, int circleSize, int iconSize, int fontSize,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        // 背景圆（选中时填充主题色）
        var bg = new Ellipse
        {
            Width = circleSize, Height = circleSize,
            Fill = BrushTransparent
        };
        // 矢量图标：24×24 原始尺寸，Grid 居中
        var icon = new Path
        {
            Data = Avalonia.Media.StreamGeometry.Parse(GetAncIcon(opt.Key)),
            Width = 24, Height = 24,
            Fill = BrushGray,
            Stretch = Avalonia.Media.Stretch.None,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        icon.Tag = opt;
        // 透明按钮覆盖整层
        var clickBtn = new Button
        {
            Width = circleSize, Height = circleSize,
            Background = BrushTransparent, BorderThickness = new Thickness(0),
            Tag = opt,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        clickBtn.Click += onClick;

        // 层叠：背景圆 → 图标 → 透明按钮
        var grid = new Grid { Width = circleSize, Height = circleSize };
        var hoverScale = new ScaleTransform(1, 1);
        grid.RenderTransform = hoverScale;
        grid.RenderTransformOrigin = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
        grid.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = Grid.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() }
        };
        grid.PointerEntered += (_, _) => { hoverScale.ScaleX = 1.08; hoverScale.ScaleY = 1.08; };
        grid.PointerExited += (_, _) => { hoverScale.ScaleX = 1; hoverScale.ScaleY = 1; };
        grid.Children.Add(bg);
        grid.Children.Add(icon);
        grid.Children.Add(clickBtn);

        var label = new TextBlock
        {
            Text = opt.Label,
            FontSize = fontSize,
            Foreground = BrushGray,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(label);

        // bg 用于填充，icon 用于图标+描边
        return (panel, bg, bg, icon, label);
    }

    private void AddToRow(Grid row, Control c, ref int col)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(c, col);
        row.Children.Add(c);
        col++;
    }

    private static void AddSpacer(Grid row, ref int col, int width)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width)));
        col++;
    }

    private void AddSeparator(Grid row, ref int col)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var sep = new Border { Width = 1, Background = BrushGray, Opacity = 0.12 };
        Grid.SetColumn(sep, col);
        row.Children.Add(sep);
        col++;
    }

    private static CornerRadius FirstLast(int i, int count, double r)
    {
        if (count == 1) return new CornerRadius(r);
        if (i == 0) return new CornerRadius(r, 0, 0, r);
        if (i == count - 1) return new CornerRadius(0, r, r, 0);
        return new CornerRadius(0);
    }

    private (Button, Border) MakeTextButton(string label, AncOption opt, int w, int h, int fontSize, CornerRadius corner, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Button
        {
            Content = label, Tag = opt, Width = w, Height = h,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = BrushTransparent, Focusable = false,
            Foreground = BrushGray, FontSize = fontSize
        };
        btn.Click += onClick;
        var bg = new Border { CornerRadius = corner, Padding = new Thickness(0), Background = BrushTransparent, Child = btn };
        return (btn, bg);
    }

    private void HighlightAnc()
    {
        var circleGray = GetCircleGray();
        var accent = _isLightTheme
            ? (IBrush)new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
            : (IBrush)BrushAccent;
        foreach (var (key, (bg, icon, label)) in _ancMainButtons)
        {
            var active = key == _ancMain;
            bg.Fill = active ? accent : circleGray;
            icon.Fill = active ? BrushWhitePure : BrushGray;
            // 主模式文字不变色
        }
        foreach (var (key, (btn, bg)) in _ancSubButtons)
        {
            var active = key == _ancLevel;
            bg.Background = active ? accent : circleGray;
            btn.Foreground = active ? BrushWhitePure : BrushGray;
        }
    }

    private void SwitchAncMain(AncOption opt)
    {
        if (!_pods.IsConnected) return;
        Log.D("UI", $"用户操作: ANC 主模式 -> {opt.Key}");
        _ancUserSetAt = DateTime.Now;
        _ancMain = opt.Key;

        if (opt.Children.Count > 0)
        {
            // 容器型：展开子模式，恢复上次选的子模式（每个父模式独立记忆）
            PopulateAncSub(opt);
            AncSubRow.IsVisible = true;
            var target = _ancLastSub.TryGetValue(opt.Key, out var last)
                && opt.Children.Any(c => c.Key == last)
                ? last : opt.Children[0].Key;
            _ancLevel = target;
            _pods.SendAnc(target);
        }
        else
        {
            // 叶子型：直接发送，收起子模式
            AncSubRow.IsVisible = false;
            _ancLevel = "";
            _pods.SendAnc(opt.Key);
        }
        HighlightAnc();
    }

    private void SwitchAncSub(AncOption opt)
    {
        if (!_pods.IsConnected) return;
        Log.D("UI", $"用户操作: ANC 子级别 -> {opt.Key}");
        _ancUserSetAt = DateTime.Now;
        _ancLevel = opt.Key;
        _ancLastSub[_ancMain] = opt.Key;
        _pods.SendAnc(opt.Key);
        HighlightAnc();
    }

    private void AncMain_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is AncOption opt) SwitchAncMain(opt);
    }

    private void AncSub_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is AncOption opt) SwitchAncSub(opt);
    }

    /// <summary>模式键 → 中文显示名（从当前型号 AncOptions 里查主模式或子模式的 Label）。</summary>
    private string AncModeLabel(string key)
    {
        foreach (var o in _ancOptions)
        {
            if (o.Key == key) return o.Label;
            foreach (var c in o.Children)
                if (c.Key == key) return c.Label;
        }
        return key;
    }

    /// <summary>把设备上报的 ANC 模式键映射到 UI 主/子选中态（完全按当前型号选项模型）。</summary>
    private void SyncAncFromState(string modeKey)
    {
        // 1) 是某个主模式（叶子）？直接选中，收起子行
        var mainOpt = _ancOptions.FirstOrDefault(o => o.Key == modeKey && o.Children.Count == 0);
        if (mainOpt != null)
        {
            _ancMain = modeKey;
            _ancLevel = "";
            AncSubRow.IsVisible = false;
            return;
        }

        // 2) 是某容器主模式的子模式？选中其父，展开子行并选中该子模式
        if (_ancChildToMain.TryGetValue(modeKey, out var parentKey))
        {
            var container = _ancOptions.FirstOrDefault(o => o.Key == parentKey);
            if (container != null)
            {
                _ancMain = parentKey;
                _ancLevel = modeKey;
                _ancLastSub[parentKey] = modeKey;
                PopulateAncSub(container);
                AncSubRow.IsVisible = true;
            }
        }
    }

    private void SpatialAudio_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is RadioButton rb && rb.Tag is string mode && _pods.IsConnected)
        {
            Log.D("UI", $"用户操作: 空间音频 -> {mode}");
            _pods.SendSpatialAudio(mode);
        }
    }

    private void CbSpatial_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbSpatial.IsChecked is { } on && _pods.IsConnected)
        {
            Log.D("UI", $"用户操作: 空间声场开关 -> {on}");
            _featureUserSetAt = DateTime.Now;
            // 音效增强互斥：开空间音效 → 需显式关游戏音效（设备不会自动关），UI 同步取消勾选并下发关闭命令
            if (on && _pods.Caps.GameSoundMutexSpatial && CbGameSound.IsChecked == true)
            {
                SetGameSoundCheckedSilent(false);
                _pods.SendGameSound(false);
                // 游戏音效被关 → 恢复 EQ 控制
                SetEqControlsEnabled(true);
            }
            // 互斥禁用：开空间音效 → 游戏音效禁用；关 → 恢复
            CbGameSound.IsEnabled = !on;
            _pods.SendSpatial(on);
        }
    }

    private void CbGame_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbGame.IsChecked is { } on && _pods.IsConnected)
        {
            Log.D("UI", $"用户操作: 游戏模式开关 -> {on}");
            _featureUserSetAt = DateTime.Now;
            _pods.SendGameMode(on, _gameModeCompat);
        }
    }

    private void CbGameSound_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbGameSound.IsChecked is { } on && _pods.IsConnected)
        {
            Log.D("UI", $"用户操作: 游戏音效开关 -> {on}");
            _featureUserSetAt = DateTime.Now;
            if (on)
            {
                // 音效增强互斥：开游戏音效 → 需显式关空间音效（设备不会自动关），UI 同步取消勾选并下发关闭命令
                if (_pods.Caps.GameSoundMutexSpatial && CbSpatial.IsChecked == true)
                {
                    SetSpatialCheckedSilent(false);
                    _pods.SendSpatial(false);
                }
                // EQ 互斥：开游戏音效 → 重置 EQ 到默认预设（"关闭"/无效果）
                if (_pods.Caps.GameSoundMutexEq)
                {
                    var def = _pods.Caps.DefaultEqPreset;
                    _pods.SendEq(def);
                    _eqCurrentPreset = def;
                    // 同步主页下拉框与面板列表选中
                    CbEq.SelectionChanged -= CbEq_SelectionChanged;
                    CbEq.SelectedItem = def;
                    CbEq.SelectionChanged += CbEq_SelectionChanged;
                    // 面板内置/自定义列表均取消选中
                    _eqSuppressListEvent = true;
                    LbEqBuiltinPresets.SelectedItem = null;
                    LbEqCustomPresets.SelectedItem = null;
                    _eqSuppressListEvent = false;
                }
                // 互斥禁用：空间音效禁用 + EQ 控制禁用
                CbSpatial.IsEnabled = false;
                SetEqControlsEnabled(false);
            }
            else
            {
                // 关游戏音效 → 恢复空间音效 + EQ 控制
                CbSpatial.IsEnabled = true;
                SetEqControlsEnabled(true);
            }
            _pods.SendGameSound(on);
        }
    }

    private void SetEqControlsEnabled(bool enabled)
    {
        // 均衡器滑块 + dB 标签
        foreach (var slider in _eqSliders.Values)
            slider.IsEnabled = enabled;
        foreach (var label in _eqDbLabels.Values)
            label.Opacity = enabled ? 0.95 : 0.35;
        // 预设列表 + 新建按钮
        LbEqBuiltinPresets.IsEnabled = enabled;
        LbEqCustomPresets.IsEnabled = enabled;
        BtnEqNew.IsEnabled = enabled;
    }

    /// <summary>不触发事件地设置游戏音效勾选态（避免互斥联动递归下发命令）。</summary>
    private void SetGameSoundCheckedSilent(bool value)
    {
        CbGameSound.IsCheckedChanged -= CbGameSound_Changed;
        CbGameSound.IsChecked = value;
        CbGameSound.IsCheckedChanged += CbGameSound_Changed;
    }

    /// <summary>不触发事件地设置空间音效勾选态。</summary>
    private void SetSpatialCheckedSilent(bool value)
    {
        CbSpatial.IsCheckedChanged -= CbSpatial_Changed;
        CbSpatial.IsChecked = value;
        CbSpatial.IsCheckedChanged += CbSpatial_Changed;
    }

    /// <summary>不触发事件地设置游戏模式勾选态（用于初始化/轮询回读，非用户操作）。</summary>
    private void SetGameCheckedSilent(bool value)
    {
        CbGame.IsCheckedChanged -= CbGame_Changed;
        CbGame.IsChecked = value;
        CbGame.IsCheckedChanged += CbGame_Changed;
    }

    /// <summary>不触发事件地设置双设备连接勾选态（用于初始化/轮询回读，非用户操作）。</summary>
    private void SetDualDeviceCheckedSilent(bool value)
    {
        CbDualDevice.IsCheckedChanged -= CbDualDevice_Changed;
        CbDualDevice.IsChecked = value;
        CbDualDevice.IsCheckedChanged += CbDualDevice_Changed;
    }

    private void CbDualDevice_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbDualDevice.IsChecked is { } on && _pods.IsConnected)
        {
            Log.D("UI", $"用户操作: 双设备开关 -> {on}");
            _featureUserSetAt = DateTime.Now;
            _pods.SendDualDevice(on);
        }
    }

    private void CbEq_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (CbEq.SelectedItem is not string name || !_pods.IsConnected) return;
        Log.D("UI", $"用户操作: EQ 预设 -> {name}");

        if (_pods.Caps.EqPresets.ContainsKey(name)
            || _pods.State.DeviceEqEntries.Any(ev => ev.Name == name))
        {
            // EQ ↔ 游戏音效互斥：选中非默认预设且游戏音效开启 → 先关游戏音效
            if (name != _pods.Caps.DefaultEqPreset
                && _pods.Caps.GameSoundMutexEq
                && CbGameSound.IsChecked == true)
            {
                Log.D("UI", $"EQ下拉: 选中预设 {name} 互斥游戏音效 → 关闭");
                _featureUserSetAt = DateTime.Now;
                SetGameSoundCheckedSilent(false);
                CbSpatial.IsEnabled = true;
                SetEqControlsEnabled(true);
                _pods.SendGameSound(false);
            }

            _eqCurrentPreset = name;
            _pods.SendEq(name);
            // 同步到 EQ 面板的预设列表
            SyncCbEqToPanel(name);
        }
        else if (_pods.Caps.CustomEqFrequencies.Length > 0)
        {
            Log.D("UI", $"EQ预设: 未知内置或设备端预设 \"{name}\"，发送当前滑块值");
            SendCurrentCustomEq();
        }
    }

    private void CbBrand_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || brand == "自动检测")
        {
            _seriesList.Clear();
            _modelList.Clear();
            return;
        }
        if (!_brandTree.TryGetValue(brand, out var series)) return;

        _seriesList.Clear();
        _seriesList.Add("（全部子系列）");
        foreach (var sn in series.Keys.OrderBy(x => x)) _seriesList.Add(sn);
        CbSeries.SelectedItem = "（全部子系列）";
    }

    private void CbSeries_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || !_brandTree.TryGetValue(brand, out var series)) return;
        var sn = CbSeries.SelectedItem as string ?? "（全部子系列）";

        _modelList.Clear();
        _modelList.Add("（全部机型）");
        var models = sn == "（全部子系列）"
            ? series.SelectMany(kv => kv.Value).Distinct().OrderBy(x => x).ToList()
            : series.TryGetValue(sn, out var list) ? list.OrderBy(x => x).ToList() : new();
        foreach (var m in models) _modelList.Add(m);
        CbModel.SelectedItem = "（全部机型）";
    }

    private void CbModel_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbModel.SelectedItem is string model && model != "（全部机型）")
        {
            Log.D("UI", $"用户操作: 手动指定机型 -> {model}");
            _modelOverride = model;
            SettingsManager.SetString("ModelOverride", model);
            SyncCaps();
        }
        else
        {
            _modelOverride = null;
            SettingsManager.SetString("ModelOverride", null);
            SyncCaps();
        }
    }

    private void SyncCaps()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;

        // 统一刷新主页调音 + EQ 面板预设列表
        RefreshAllEqViews();

        BuildAncUi(caps);
        SpatialAudioPanel.IsVisible = caps.HasSpatialAudio;
        // CbSpatial.IsVisible = caps.HasSpatialSound;
        // CbDualDevice.IsVisible = caps.HasDualDevice;
        // CbGame.IsVisible = caps.HasGameMode;
        // CbGameSound.IsVisible = caps.HasGameSound;

        // 全部占位控件暂不按能力隐藏，全量显示便于验证布局
        CbSpatial.IsVisible = caps.HasSpatialSound;
        CbDualDevice.IsVisible = caps.HasDualDevice;
        CbGame.IsVisible = caps.HasGameMode;
        CbGameSound.IsVisible = caps.HasGameSound;

        // 以下功能后端未实现，暂时隐藏
        // CbBassEngine.IsVisible = caps.HasBassEngine;
        // CbVocalEnhance.IsVisible = caps.HasVocalEnhance;
        // CbHearingEnhance.IsVisible = caps.HasHearingEnhancement;
        // CbLongPower.IsVisible = caps.HasLongPowerMode;
        // CbWearDetection.IsVisible = caps.HasWearDetection;
        // CbSpineHealth.IsVisible = caps.HasSpineHealth;
        // BtnFindDevice.IsVisible = caps.HasFindDevice;

        ModelNote.Text = _modelOverride == null
            ? $"当前自动识别: {_pods.Caps.ModelName}"
            : $"已手动指定: {caps.ModelName}";

        UpdateTitle();
        if (_pods.State.Connected)
        {
            StatusText.Text = $"已连接 — {caps.ModelName}";
            StatusText.Foreground = BrushLightGreen;
            StatusDot.IsVisible = true;
            StatusText.IsVisible = true;
            if (caps.HasDualDevice)
            {
                StatusDot.IsVisible = false;
                StatusText.IsVisible = false;
                SyncMultiDeviceList();
            }
        }
    }

    private void CbGameMode_Changed(object? s, SelectionChangedEventArgs e)
    {
        _gameModeCompat = CbGameMode.SelectedIndex == 1;
        SettingsManager.SetBool("GameModeCompat", _gameModeCompat);
    }

    private void CbTheme_Changed(object? s, SelectionChangedEventArgs e)
    {
        var idx = CbTheme.SelectedIndex;
        Log.D("UI", $"用户操作: 切换主题 -> {idx}");
        ApplyTheme(idx);
        SettingsManager.SetInt("Theme", idx);
    }

    private void ApplyTheme(int index)
    {
        var theme = SukiTheme.GetInstance();
        switch (index)
        {
            case 0:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Default);
                _isLightTheme = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
                break;
            case 1:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Dark);
                _isLightTheme = false;
                break;
            case 2:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Light);
                _isLightTheme = true;
                break;
        }
        RefreshThemeColors();
    }

    private IBrush GetCircleGray() => _isLightTheme
        ? new SolidColorBrush(Color.FromArgb(0x15, 0x00, 0x00, 0x00))
        : new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

    private void RefreshThemeColors()
    {
        // 清除之前可能残留的 SukiUI 资源覆盖，让 SukiUI 原生主题系统接管
        // （按钮、ComboBox 等控件的 Background 绑定到 SukiBackground，
        //   如果在 Window 级覆盖会导致按钮背景与窗口背景混为一体）
        Resources.Remove("SukiBackground");
        Resources.Remove("SukiCardBackground");

        // 窗口背景：浅色微灰，深色透明
        Background = _isLightTheme
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA))
            : BrushTransparent;

        // 深色毛玻璃(10%白) / 浅色纯白
        var glassBg = _isLightTheme
            ? (IBrush)new SolidColorBrush(Colors.White)
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Resources["GlassCardBg"] = glassBg;

        // 侧边栏：浅色纯白 / 深色毛玻璃
        var sidebarBg = _isLightTheme
            ? (IBrush)new SolidColorBrush(Colors.White)
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        // 侧边栏底框
        SidebarBorder.Background = sidebarBg;

        // 侧边栏选中高亮：浅色灰底，深色微白
        var selectedBg = _isLightTheme
            ? (IBrush)new SolidColorBrush(Color.FromArgb(0x0C, 0x00, 0x00, 0x00))
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        Resources["SidebarSelectedBg"] = selectedBg;

        // 弹窗遮罩：深色半透明黑 / 浅色半透明白
        Resources["DialogOverlayBg"] = _isLightTheme
            ? (IBrush)new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00))
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        // 重置对话框确认按钮颜色，避免主题切换后残留
        if (DialogConfirmBtn.IsVisible)
            DialogConfirmBtn.Background = Brushes.Transparent;

        RefreshEqBandVisuals();
        HighlightAnc();
        var s = _pods.State;
        StatusDot.Fill = s.Connected ? BrushGreen : BrushRed;
        StatusText.Foreground = s.Connected ? BrushLightGreen : BrushLightRed;
        if (!_realClose) OnStateChanged();
    }

    private static void UpdateGlassCards(ScrollViewer sv, IBrush bg)
    {
        if (sv.Content is StackPanel sp)
            foreach (var child in sp.Children)
                if (child is Border b && b.Classes.Contains("glassCard"))
                    b.Background = bg;
    }

    private void TbCustomName_Changed(object? s, TextChangedEventArgs e)
    {
        SettingsManager.SetString("CustomName",
            string.IsNullOrWhiteSpace(TbCustomName.Text) ? null : TbCustomName.Text.Trim());
        UpdateTitle();
    }

    /// <summary>
    /// 获取设备显示名称：优先使用自定义名称，否则回退到设备型号名。
    /// 与 UpdateTitle 保持一致的优先级逻辑。
    /// </summary>
    private string GetDeviceDisplayName()
    {
        var custom = (TbCustomName.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(custom)) return custom;
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;
        return caps.ModelName;
    }

    /// <summary>根据用户设置获取弹窗时长（毫秒）。默认=5000ms。</summary>
    private int GetToastDuration()
    {
        var idx = SettingsManager.GetInt("ToastDuration", 2);
        return idx switch { 0 => 3000, 1 => 4000, 3 => 6000, 4 => 7000, 5 => 8000, _ => 5000 };
    }

    private void UpdateTitle()
    {
        var name = GetDeviceDisplayName();
        Title = name;

        var s = _pods.State;
        var parts = new List<string>();
        var bl = MergeCharge(s.Battery.GetValueOrDefault("L"), s.WearingL);
        var br = MergeCharge(s.Battery.GetValueOrDefault("R"), s.WearingR);
        if (bl is { } lb) parts.Add($"L:{lb.Lvl}%{(lb.Chg ? "(充)" : "")}");
        if (br is { } rb) parts.Add($"R:{rb.Lvl}%{(rb.Chg ? "(充)" : "")}");
        if (s.Battery.GetValueOrDefault("C") is { } cb) parts.Add($"C:{cb.Level}%{(cb.Charging ? "(充)" : "")}");
        UpdateTrayTooltip(parts.Count > 0 ? $"{name}\n{string.Join(" ", parts)}" : name);
    }

    private void NavHome_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("home");
    private void NavEq_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("eq");
    private void NavDeviceInfo_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("deviceinfo");
    private void NavLog_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("log");
    private void NavSettings_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("settings");

    private void ShowPage(string page)
    {
        MainPanel.IsVisible = page == "home";
        EqPanel.IsVisible = page == "eq";
        if (page == "eq" && _pods.IsConnected) _pods.SendQueryEqAll();
        DeviceInfoPanel.IsVisible = page == "deviceinfo";
        SettingsPanel.IsVisible = page == "settings";
        LogPanel.IsVisible = page == "log";
        AboutPanel.IsVisible = page == "about";

        NavHome.Classes.Remove("selected");
        NavEq.Classes.Remove("selected");
        NavDeviceInfo.Classes.Remove("selected");
        NavSettings.Classes.Remove("selected");

        if (page == "home") NavHome.Classes.Add("selected");
        else if (page == "eq") NavEq.Classes.Add("selected");
        else if (page == "deviceinfo") NavDeviceInfo.Classes.Add("selected");
        else NavSettings.Classes.Add("selected");

        if (page == "deviceinfo") RefreshDeviceInfo();
        if (page == "eq") RefreshEqPresetList();
        if (page == "log") RefreshLogView();
    }

    private void About_Click(object? s, RoutedEventArgs e) => ShowPage("about");
    private void AboutBack_Click(object? s, RoutedEventArgs e) => ShowPage("settings");

    private async void BtnFeedback_Click(object? s, RoutedEventArgs e)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "提交反馈";
        DialogMessage.FontSize = 14;
        DialogMessage.Text = "点击「确认」后将会把日志导出到桌面，同时打开浏览器反馈页面，请按要求填写标题、内容并上传日志文件。\n\n如果无法连接至 GitHub，可点击「GitLab」按钮前往 GitLab 进行反馈。";
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.IsVisible = true;
        DialogSkipBtn.Content = "GitLab";
        DialogSkipBtn.IsVisible = true;
        DialogConfirmBtn.Content = "确认";
        DialogOverlay.IsVisible = true;

        var ok = await _confirmTcs.Task;
        if (!ok) return;

        ExportFeedback("https://github.com/Zhaoyi-ya/OppoPodsManager/issues/new");
    }

    private void ExportFeedback(string url)
    {
        try
        {
            var os = RuntimeInformation.OSDescription;
            var ver = VersionText.Text ?? "unknown";
            var model = string.IsNullOrEmpty(_pods.Caps.ModelName) ? "unknown" : _pods.Caps.ModelName;
            var connected = _pods.State.Connected;
            var stateText = connected ? "connected" : "disconnected";
            var bat = connected ? string.Join(" ", _pods.State.Battery.Select(kv => $"{kv.Key}{kv.Value?.Level ?? -1}%")) : "N/A";
            var date = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var header = $"""
--- 系统信息 ---
版本: {ver}
操作系统: {os}
设备型号: {model}
连接状态: {stateText}
电量: {bat}

""";

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            // Linux 可能返回空，退回到用户主目录
            if (string.IsNullOrEmpty(desktop))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(desktop))
                desktop = Environment.CurrentDirectory;

            var fileName = $"OPPOPods_feedback_{date}.zip";
            var filePath = System.IO.Path.Combine(desktop, fileName);

            // 打包所有历史日志 + 系统信息为 ZIP
            _logManager.ExportLogsAsZip(filePath, header);

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            _ = Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog($"日志已打包导出到：{filePath}\n\n浏览器已打开反馈页面，请填写描述并上传该 ZIP 文件", "提交反馈"));
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "ExportFeedback", ex);
        }
    }

    private void OpenUrl_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ========== EQ 调节 ==========

    private string _eqCurrentPreset = "";
    private int _eqCurrentId; // 当前编辑的设备端预设 eqId，0=新建
    private bool _eqSuppressListEvent;
    private bool _eqSuppressSliderEvent;
    private int _lastDeviceEqCount = -1;
    private readonly Dictionary<int, Slider> _eqSliders = new();
    private readonly Dictionary<int, TextBlock> _eqDbLabels = new();
    private readonly Dictionary<int, TextBlock> _eqFrequencyLabels = new();
    private readonly List<TextBlock> _eqScaleLabels = new();
    /// <summary>进入编辑时的滑块快照，用于重置。</summary>
    private double[] _eqBackupSliders = Array.Empty<double>();

    private int[] GetCurrentEqFrequencies()
    {
        var capsFreqs = _pods.Caps.CustomEqFrequencies;
        if (capsFreqs.Length > 0) return capsFreqs;

        var deviceFreqs = _pods.State.DeviceEqEntries
            .FirstOrDefault(e => e.Frequencies.Length > 0)?.Frequencies;
        return deviceFreqs is { Length: > 0 }
            ? deviceFreqs
            : [62, 250, 1000, 4000, 8000, 16000];
    }

    private void BuildEqBandControls(int[] frequencies)
    {
        EqBandsPanel.Children.Clear();
        _eqSliders.Clear();
        _eqDbLabels.Clear();
        _eqFrequencyLabels.Clear();
        _eqScaleLabels.Clear();

        if (frequencies.Length == 0) return;

        var topScale = new TextBlock { Text = "+6", FontSize = 10, FontWeight = FontWeight.Medium, Foreground = BrushWhite, Opacity = 0.78, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var midScale = new TextBlock { Text = " 0", FontSize = 10, FontWeight = FontWeight.Medium, Foreground = BrushWhite, Opacity = 0.68, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var bottomScale = new TextBlock { Text = "-6", FontSize = 10, FontWeight = FontWeight.Medium, Foreground = BrushWhite, Opacity = 0.78, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        _eqScaleLabels.Add(topScale);
        _eqScaleLabels.Add(midScale);
        _eqScaleLabels.Add(bottomScale);

        var scaleGrid = new Grid
        {
            Height = 180,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                topScale,
                midScale,
                bottomScale,
            }
        };
        Grid.SetRow(scaleGrid.Children[1], 1);
        Grid.SetRow(scaleGrid.Children[2], 2);
        EqBandsPanel.Children.Add(scaleGrid);

        foreach (var frequency in frequencies)
        {
            if (_eqSliders.ContainsKey(frequency)) continue;

            var dbLabel = new TextBlock
            {
                Text = "0",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushWhite,
                Opacity = 0.95,
                Margin = new Thickness(0, 0, 0, 2),
            };
            var slider = new Slider
            {
                Width = 36,
                Height = 180,
                Orientation = Orientation.Vertical,
                Minimum = -6,
                Maximum = 6,
                Value = 0,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Tag = frequency,
            };
            slider.PropertyChanged += EqSlider_Changed;

            var frequencyLabel = new TextBlock
            {
                Text = FormatEqFrequency(frequency),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushWhite,
                Opacity = 0.98,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var band = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    dbLabel,
                    slider,
                    frequencyLabel,
                }
            };

            _eqSliders[frequency] = slider;
            _eqDbLabels[frequency] = dbLabel;
            _eqFrequencyLabels[frequency] = frequencyLabel;
            EqBandsPanel.Children.Add(band);
        }
        RefreshEqBandVisuals();
    }

    private static string FormatEqFrequency(int frequency)
    {
        if (frequency >= 1000 && frequency % 1000 == 0) return $"{frequency / 1000}k";
        return frequency >= 1000 ? $"{frequency / 1000.0:0.#}k" : frequency.ToString();
    }

    private void UpdateEqDbLabel(int frequency, double value)
    {
        if (!_eqDbLabels.TryGetValue(frequency, out var label)) return;
        var db = (int)Math.Round(value);
        var sign = db > 0 ? "+" : "";
        label.Text = $"{sign}{db}";
        label.Foreground = db switch
        {
            > 0 => BrushLightGreen,
            < 0 => BrushLightRed,
            _ => BrushWhite,
        };
        label.Opacity = 0.95;
    }

    private void RefreshEqBandVisuals()
    {
        foreach (var label in _eqScaleLabels)
        {
            label.Foreground = BrushWhite;
            label.Opacity = 0.78;
        }
        foreach (var label in _eqFrequencyLabels.Values)
        {
            label.Foreground = BrushWhite;
            label.Opacity = 0.98;
        }
        foreach (var (frequency, slider) in _eqSliders)
            UpdateEqDbLabel(frequency, slider.Value);
    }

    /// <summary>滑块值变更 → 更新对应 dB 标签，触发防抖预览下发。</summary>
    private void EqSlider_Changed(object? s, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;
        if (s is not Slider slider) return;
        if (slider.Tag is not int frequency) return;

        UpdateEqDbLabel(frequency, slider.Value);
        if (_eqSuppressSliderEvent) return;

        // 防抖 150ms 后下发自定义 EQ（实时预览）
        _eqDebounceTimer?.Stop();
        _eqDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) =>
            {
                _eqDebounceTimer?.Stop();
                SendCurrentCustomEq();
            });
        _eqDebounceTimer.Start();
    }

    // ---- 预设列表 ----

    /// <summary>向 LogManager 追加一行日志。</summary>
    public void AppendLog(string tag, string msg)
    {
        _logManager.AppendLog(tag, msg);
    }

    /// <summary>可合并的轮询发送行（只合并发送，不合并收到）。</summary>
    private static readonly HashSet<string> _mergePatterns = new()
    {
        "查询电量", "查询电池详情", "查询降噪", "查询降噪状态",
        "查询功能开关", "查询预设数据", "查询所有预设", "查询多设备",
        "查询固件", "查询空间音效", "注册通知", "查询多设备列表",
    };

    /// <summary>刷新日志列表视图（支持简化版/完整版切换）。</summary>
    private void RefreshLogView()
    {
        var lines = _logManager.GetRecentLines();
        if (lines.Count == 0) return;

        LbLog.Items.Clear();

        if (_logSimplified)
        {
            // 简化版：翻译 + 合并轮询
            var entries = new List<string>();
            foreach (var line in lines)
            {
                var t = TranslateLog(line);
                if (t.Length > 0) entries.Add(t);
            }

            var merged = new List<string>();
            var pollBuf = new List<string>();
            string? pollTs = null;
            void FlushPoll()
            {
                if (pollBuf.Count > 0)
                {
                    var summary = pollBuf.Count <= 2
                        ? string.Join("，", pollBuf)
                        : $"后台轮询发送（{string.Join("、", pollBuf.Distinct())}，共{pollBuf.Count}次）";
                    merged.Add($"{pollTs}  {summary}");
                    pollBuf.Clear();
                    pollTs = null;
                }
            }
            foreach (var entry in entries)
            {
                var ts = entry.Length >= 12 ? entry[..12].TrimEnd() : "";
                var msg = entry.Length > 13 ? entry[14..] : entry;
                if (_mergePatterns.Contains(msg))
                {
                    pollBuf.Add(msg);
                    pollTs ??= ts;
                }
                else
                {
                    FlushPoll();
                    merged.Add(entry);
                }
            }
            FlushPoll();

            foreach (var m in merged) LbLog.Items.Add(m);
        }
        else
        {
            // 完整版：原始行
            foreach (var line in lines)
                LbLog.Items.Add(line);
        }

        // 自动跟随最新日志
        if (_logAutoScroll && _logScrollViewer != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_logScrollViewer != null)
                    _logScrollViewer.Offset = new Vector(_logScrollViewer.Offset.X,
                        _logScrollViewer.Extent.Height);
            }, DispatcherPriority.Loaded);
        }
    }
    /// 统一刷新主页调音下拉框 + EQ 面板预设列表（共用一套数据源）。
    /// 自动恢复当前选中项。
    /// </summary>
    private void RefreshAllEqViews()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;

        // ---- 主页调音下拉框 ----
        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.Items.Clear();
        foreach (var kv in caps.EqPresets)
            CbEq.Items.Add(kv.Key);
        foreach (var e in _pods.State.DeviceEqEntries)
            if (!string.IsNullOrWhiteSpace(e.Name) && !CbEq.Items.Contains(e.Name))
                CbEq.Items.Add(e.Name);
        // 恢复选中
        if (!string.IsNullOrEmpty(_eqCurrentPreset) && CbEq.Items.Contains(_eqCurrentPreset))
            CbEq.SelectedItem = _eqCurrentPreset;
        CbEq.SelectionChanged += CbEq_SelectionChanged;

        // ---- EQ 面板预设列表 ----
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectionChanged -= EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged -= EqCustomPresets_Changed;
        LbEqBuiltinPresets.Items.Clear();
        LbEqCustomPresets.Items.Clear();

        // 左：系统预设
        foreach (var kv in caps.EqPresets)
            LbEqBuiltinPresets.Items.Add(new EqPresetItem { Name = kv.Key, IsCustom = false });

        // 右：自定义
        foreach (var e in _pods.State.DeviceEqEntries)
        {
            if (!string.IsNullOrWhiteSpace(e.Name) && !caps.EqPresets.ContainsKey(e.Name))
                LbEqCustomPresets.Items.Add(new EqPresetItem { Name = e.Name, IsCustom = false, EqId = e.EqId });
        }

        // 恢复选中项
        if (!string.IsNullOrEmpty(_eqCurrentPreset))
        {
            SyncCbEqToPanel(_eqCurrentPreset);
            // 如果是自定义/设备端预设，显示均衡器滑块并加载已保存的增益值（不重复发送命令）
            var selItem = LbEqBuiltinPresets.SelectedItem as EqPresetItem
                       ?? LbEqCustomPresets.SelectedItem as EqPresetItem;
            if (selItem is { IsDeviceEntry: true } or { IsCustom: true })
                ApplyEqSelection(selItem, sendToDevice: false);
        }

        LbEqBuiltinPresets.SelectionChanged += EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged += EqCustomPresets_Changed;
        _eqSuppressListEvent = false;

        // 保存后重新获取当前预设的 eqId（优先按 _eqCurrentId 匹配，其次按名称）
        if (!string.IsNullOrEmpty(_eqCurrentPreset))
        {
            var entry = _eqCurrentId > 0
                ? _pods.State.DeviceEqEntries.FirstOrDefault(e => e.EqId == _eqCurrentId)
                : _pods.State.DeviceEqEntries.FirstOrDefault(e => e.Name == _eqCurrentPreset);
            if (entry != null) _eqCurrentId = entry.EqId;
        }

        // 自定义预设未满才显示新建按钮
        var maxCustom = caps.CustomEqMaxPresets > 0 ? caps.CustomEqMaxPresets : 3;
        BtnEqNew.IsVisible = _pods.State.DeviceEqEntries.Count < maxCustom;
    }

    // 兼容旧方法（均转接到统一入口）
    private void RefreshEqPresetList() => RefreshAllEqViews();
    private void RefreshMainEqCombo() => RefreshAllEqViews();

    /// <summary>系统预设选中 → 发送切换、隐藏滑块。</summary>
    private void EqBuiltinPresets_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_eqSuppressListEvent) return;
        if (LbEqBuiltinPresets.SelectedItem is not EqPresetItem item) return;

        // 交叉取消自定义列表的选中
        _eqSuppressListEvent = true;
        LbEqCustomPresets.SelectedItem = null;
        _eqSuppressListEvent = false;

        ApplyEqSelection(item);
    }

    /// <summary>自定义预设选中 → 交叉取消系统列表的选中。</summary>
    private void EqCustomPresets_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_eqSuppressListEvent) return;
        if (LbEqCustomPresets.SelectedItem is not EqPresetItem item) return;

        // 交叉取消系统列表的选中
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectedItem = null;
        _eqSuppressListEvent = false;

        ApplyEqSelection(item);
    }

    /// <summary>预设选中 → 内置发送切换、隐藏滑块；自定义/设备端展开编辑。</summary>
    private void ApplyEqSelection(EqPresetItem item, bool sendToDevice = true)
    {
        // EQ ↔ 游戏音效互斥：选中非默认预设且游戏音效开启 → 先关游戏音效
        if (_pods.IsConnected && _pods.Caps.GameSoundMutexEq
            && item.Name != _pods.Caps.DefaultEqPreset
            && CbGameSound.IsChecked == true)
        {
            Log.D("UI", $"EQ面板: 选中预设 {item.Name} 互斥游戏音效 → 关闭");
            _featureUserSetAt = DateTime.Now;
            SetGameSoundCheckedSilent(false);
            CbSpatial.IsEnabled = true;
            SetEqControlsEnabled(true);
            _pods.SendGameSound(false);
        }

        _eqCurrentPreset = item.Name;

        if (_pods.IsConnected && sendToDevice)
        {
            Log.D("UI", $"EQ面板: 切换预设 -> {item.Name}");
            _pods.SendEq(item.Name);
        }

        // 内置预设：直接生效，不显示滑块
        if (!item.IsCustom && !item.IsDeviceEntry)
        {
            EqSliderCard.IsVisible = false;
            EqHintText.Text = $"已切换至预设「{item.Name}」";
            // 同步主页调音下拉框（抑制事件避免循环）
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.SelectedItem = item.Name;
            CbEq.SelectionChanged += CbEq_SelectionChanged;
            return;
        }

        // 自定义/设备端预设：显示滑块编辑
        EqSliderCard.IsVisible = true;
        // 尝试加载设备保存的增益值（优先按 eqId，其次按名称）
        var entry = item.EqId > 0
            ? _pods.State.DeviceEqEntries.FirstOrDefault(d => d.EqId == item.EqId)
            : _pods.State.DeviceEqEntries.FirstOrDefault(d => d.Name == item.Name);
        if (entry is { Gains.Length: > 0, Frequencies.Length: > 0 })
        {
            BuildEqBandControls(entry.Frequencies);
            _eqSuppressSliderEvent = true;
            for (int i = 0; i < entry.Frequencies.Length; i++)
                if (_eqSliders.TryGetValue(entry.Frequencies[i], out var sld))
                {
                    sld.Value = entry.Gains[i];
                    UpdateEqDbLabel(entry.Frequencies[i], entry.Gains[i]);
                }
            _eqSuppressSliderEvent = false;
        }
        else SetAllEqSliders(0);
        SnapshotSliders();
        _eqCurrentId = item.EqId;
        Log.D("UI", $"EQ选中: name={item.Name} eqId={_eqCurrentId} isCustom={item.IsCustom} isDev={item.IsDeviceEntry}");
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = $"编辑「{item.Name}」— 拖拽滑块调整";
        // 同步主页调音下拉框（抑制事件避免循环）
        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.SelectedItem = item.Name;
        CbEq.SelectionChanged += CbEq_SelectionChanged;
    }

    // ---- 辅助 ----

    /// <summary>双向同步：将 EQ 面板的选中状态同步到主页调音下拉框。</summary>
    private void SyncCbEqToPanel(string name)
    {
        _eqSuppressListEvent = true;
        // 先在系统预设列表里找
        foreach (var item in LbEqBuiltinPresets.Items.OfType<EqPresetItem>())
        {
            if (item.Name == name) { LbEqBuiltinPresets.SelectedItem = item; LbEqCustomPresets.SelectedItem = null; _eqSuppressListEvent = false; return; }
        }
        // 再在自定义列表里找
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
        {
            if (item.Name == name) { LbEqCustomPresets.SelectedItem = item; LbEqBuiltinPresets.SelectedItem = null; _eqSuppressListEvent = false; return; }
        }
        _eqSuppressListEvent = false;
    }

    private bool IsBuiltinPreset(string name) =>
        _pods.Caps.EqPresets.ContainsKey(name);

    private static bool IsValidEqName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\u4e00-\u9fa5a-zA-Z0-9]+$");
    }

    /// <summary>新建/保存后立即在自定义列表中追加并选中，不等设备响应。</summary>
    private void AddCustomPresetToList(string name, byte eqId = 0)
    {
        // 避免重复——已有同名项则只更新 eqId 并选中（设备响应后刷新）
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
            if (item.Name == name) { item.EqId = eqId; LbEqCustomPresets.SelectedItem = item; return; }

        var newItem = new EqPresetItem { Name = name, IsCustom = true, EqId = eqId };
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.Items.Add(newItem);
        LbEqCustomPresets.SelectedItem = newItem;
        _eqSuppressListEvent = false;
        // 显示均衡器
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
    }

    private void SetAllEqSliders(double value)
    {
        _eqSuppressSliderEvent = true;
        foreach (var (frequency, slider) in _eqSliders)
        {
            slider.Value = value;
            UpdateEqDbLabel(frequency, value);
        }
        _eqSuppressSliderEvent = false;
    }

    /// <summary>将当前 UI 滑块值映射到设备频率数组。</summary>
    private int[] SliderToGains()
    {
        var freqs = GetCurrentEqFrequencies();
        if (_eqSliders.Count != freqs.Length || freqs.Any(f => !_eqSliders.ContainsKey(f)))
            BuildEqBandControls(freqs);

        var gains = new int[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
            gains[i] = _eqSliders.TryGetValue(freqs[i], out var sld) ? (int)Math.Round(sld.Value) : 0;
        return gains;
    }

    /// <summary>向设备发送当前 UI 滑块值作为自定义 EQ 实时预览。
    /// 仅对已有预设（有 eqId）用 action=2 更新；新建预设在保存前不下发，避免设备重复建项。</summary>
    private void SendCurrentCustomEq()
    {
        if (!_pods.IsConnected || _pods.Caps.CustomEqFrequencies.Length == 0) return;
        if (_eqCurrentId == 0 || string.IsNullOrEmpty(_eqCurrentPreset))
        {
            Log.D("UI", "EQ预览: 新建预设保存前不下发，避免设备重复建项");
            return;
        }
        _pods.UpdateCustomEq((byte)_eqCurrentId, SliderToGains(), _eqCurrentPreset);
    }

    // ---- 按钮操作 ----

    private void BtnEqCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 仅当前编辑自定义/设备端预设时生效
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        SetAllEqSliders(0);
        SnapshotSliders();
        EqHintText.Text = "已重置，点击保存生效";
    }

    private async void BtnEqNew_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? name;
        do
        {
            name = await ShowPromptDialog("新建自定义 EQ", "自定义", "名称仅支持中文/英文/数字，不含空格或特殊字符");
            if (string.IsNullOrEmpty(name)) return;
            if (IsValidEqName(name)) break;
            await ShowCheckResultDialog("名称仅支持中文/英文/数字，不能含空格或特殊字符", "名称无效");
        } while (true);
        _eqCurrentPreset = name;
        _eqCurrentId = 0;
        BuildEqBandControls(GetCurrentEqFrequencies());
        SetAllEqSliders(0);
        SnapshotSliders();
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = $"新建「{name}」— 拖拽滑块调节后保存";
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.SelectedItem = null;

        // 立即加入自定义列表并选中
        AddCustomPresetToList(name);
    }

    private void BtnEqSave_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        if (!IsValidEqName(_eqCurrentPreset))
        {
            EqHintText.Text = "名称仅支持中文/英文/数字，不含空格与特殊字符";
            return;
        }
        var selectedEqId = (LbEqCustomPresets.SelectedItem as EqPresetItem)?.EqId ?? 0;
        var saveEqId = selectedEqId > 0 ? selectedEqId : _eqCurrentId;
        if (saveEqId > 0) _eqCurrentId = saveEqId;
        Log.D("UI", $"EQ保存: name={_eqCurrentPreset} eqId={saveEqId}");
        DoSaveEqPreset(_eqCurrentPreset, saveEqId);
        SnapshotSliders();

        // 立即添加到自定义列表并选中，不等设备异步响应
        AddCustomPresetToList(_eqCurrentPreset, (byte)saveEqId);
    }

    private void SnapshotSliders()
    {
        _eqBackupSliders = GetCurrentEqFrequencies()
            .Select(f => _eqSliders.TryGetValue(f, out var slider) ? slider.Value : 0)
            .ToArray();
    }

    private async void EqListItemDelete_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not int eqId) return;
        // 从列表项取显示名称（仅用于确认对话框提示）
        var listItem = btn.DataContext as EqPresetItem;
        var displayName = listItem?.Name ?? $"eqId={eqId}";

        if (!await ShowConfirmDialog("删除预设", $"确定要删除预设「{displayName}」吗？")) return;

        if (eqId > 0)
        {
            // 按 eqId 删除设备端条目
            _pods.DeleteEq(eqId);
            RemoveCustomEqLocally(eqId, displayName);
        }
        else
        {
            // eqId=0（新建后未同步的条目）：尝试按名称在设备端查找
            var removedEqId = 0;
            if (!string.IsNullOrEmpty(displayName))
            {
                var devEntry = _pods.State.DeviceEqEntries.FirstOrDefault(ev => ev.Name == displayName);
                if (devEntry != null)
                {
                    removedEqId = devEntry.EqId;
                    _pods.DeleteEq(devEntry.EqId);
                }
                else
                {
                    Log.D("UI", $"EQ面板: 预设「{displayName}」设备端不存在，仅从列表移除");
                }
            }
            RemoveCustomEqLocally(removedEqId, displayName);
        }

        if (_eqCurrentPreset == displayName)
        {
            _eqCurrentPreset = "";
            SetAllEqSliders(0);
        }
        EqHintText.Text = $"「{displayName}」已删除";
    }

    private void RemoveCustomEqLocally(int eqId, string displayName)
    {
        // 本机 SPP 设备的 0x8122 不回读权威列表（常返回空），删除必须乐观更新本地状态，
        // 否则后续 OnStateChanged → RefreshAllEqViews 会从未更新的 DeviceEqEntries 把已删项重建出来。
        _pods.State.DeviceEqEntries = _pods.State.DeviceEqEntries
            .Where(e => eqId > 0 ? e.EqId != eqId : e.Name != displayName)
            .ToList();

        var item = LbEqCustomPresets.Items.OfType<EqPresetItem>()
            .FirstOrDefault(e => eqId > 0 ? e.EqId == eqId : e.Name == displayName);
        if (item != null)
            LbEqCustomPresets.Items.Remove(item);

        if (CbEq.Items.Contains(displayName))
            CbEq.Items.Remove(displayName);
    }

    private void DoSaveEqPreset(string name, int eqId = 0)
    {
        _eqCurrentPreset = name;
        if (_pods.IsConnected)
        {
            // 已有预设 → action=2 更新；新预设 → action=1 新建（APK: B8.c.i action 2 vs 1）
            if (eqId > 0)
                _pods.UpdateCustomEq((byte)eqId, SliderToGains(), name);
            else
                _pods.SendCustomEq(SliderToGains(), name);
        }
        EqHintText.Text = $"已保存「{name}」到设备";
    }

    // ---- 设备详情 ----

    /// <summary>刷新设备详情页（固件、编解码器）。</summary>
    private void RefreshDeviceInfo()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;

        DiDeviceName.Text = caps.ModelName;
        DiFirmware.Text = FormatFirmware(_pods.State.FirmwareVersion);
        DiCodec.Text = OppoProtocol.CodecName(_pods.State.CodecType);
    }

    /// <summary>固件版本 CSV → 显示格式：138.138.105。</summary>
    private static string FormatFirmware(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "-";
        var parts = raw.Split(',');
        if (parts.Length < 3) return raw;

        var versions = new Dictionary<int, string>();
        for (int i = 0; i + 2 < parts.Length; i += 3)
        {
            if (int.TryParse(parts[i], out var devType) && int.TryParse(parts[i + 2], out var val))
                versions[devType] = val.ToString();
        }
        // 按设备类型排序输出
        var ordered = versions.OrderBy(kv => kv.Key).Select(kv => kv.Value);
        return string.Join(".", ordered);
    }

    // ---- 浮层对话框（Avalonia 原生遮罩，不创建新窗口）----

    private TaskCompletionSource<string?>? _promptTcs;
    private TaskCompletionSource<bool>? _confirmTcs;
    private string _updatePendingVersion = ""; // 当前提示的新版本号，供跳过使用

    /// <summary>浮层命名输入。</summary>
    private async Task<string?> ShowPromptDialog(string title, string defaultText = "", string hint = "")
    {
        _promptTcs = new TaskCompletionSource<string?>();
        _confirmTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = string.IsNullOrEmpty(hint) ? "请输入预设名称：" : hint;
        DialogInput.IsVisible = true;
        DialogInput.Text = defaultText;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = "保存";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        DialogInput.Focus();
        DialogInput.SelectAll();

        return await _promptTcs.Task;
    }

    /// <summary>浮层确认对话框。</summary>
    private async Task<bool> ShowConfirmDialog(string title, string message)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = "确认删除";
        DialogConfirmBtn.Background = new SolidColorBrush(Color.Parse("#CCE81123"));
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        return await _confirmTcs.Task;
    }

    private void DialogSkip_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DialogSkipBtn.Content is string label && label == "GitLab")
        {
            DialogOverlay_Close();
            _confirmTcs?.TrySetResult(false);
            ExportFeedback("https://jihulab.com/zhaoyi-ya-group/oppo-pods-manager/-/work_items/new");
            return;
        }
        SettingsManager.SetString("SkippedVersion", _updatePendingVersion);
        DialogOverlay_Close();
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogOverlay_Close()
    {
        DialogOverlay.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
    }

    private void DialogCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DialogOverlay_Close();
        _promptTcs?.TrySetResult(null);
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogConfirm_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DialogOverlay_Close();

        if (_promptTcs != null)
        {
            var text = DialogInput.Text?.Trim();
            _promptTcs.TrySetResult(string.IsNullOrEmpty(text) ? null : text);
        }
        else if (_confirmTcs != null)
        {
            _confirmTcs.TrySetResult(true);
        }
    }

    private void Reconnect_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 点击重连");
        _pods.Disconnect();
    }

    private void ResetUi()
    {
        SetBatLabel(LeftLabel, null);
        SetBatLabel(RightLabel, null);
        SetBatLabel(CaseLabel, null);
        WearStatus.Text = "";
        AncSubRow.IsVisible = false;
        CbSpatial.IsChecked = false;
        CbGame.IsChecked = false;
        CbGameSound.IsChecked = false;
        // 恢复互斥禁用状态
        CbSpatial.IsEnabled = true;
        CbGameSound.IsEnabled = true;
        SetEqControlsEnabled(true);
    }

    private void OnWindowClosing(object? s, WindowClosingEventArgs e)
    {
        // 真正退出（QuitApplication 已设 _realClose）→ 放行
        if (_realClose) return;

        // 托盘已启用 → 隐藏而非关闭
        if (CbTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // 托盘未启用 → 必须显式退出
        // TrayIcon 会让进程保活，单纯 Close() 不会终止进程，后台服务卡住
        // 之后若误点托盘图标调 Show() → "Cannot re-show a closed window" 崩溃
        e.Cancel = true;
        QuitApplication();
    }

    /// <summary>统一退出：停止轮询、释放设备、隐藏托盘、终止进程。</summary>
    private void QuitApplication()
    {
        _realClose = true;
        _sessionCts?.Cancel();
        _pods.Dispose();
        if (_trayIcon != null) _trayIcon.IsVisible = false;
        Environment.Exit(0);
    }

    // ===== 设备列表 =====
    // 记录上次渲染的列表签名：SyncMultiDeviceList 会被每次 StateChanged（含电量/ANC 轮询，约2s一次）触发，
    // 若每次都 Clear+重建，正打开的右键菜单会因宿主 Border 被销毁而闪退（"稍微移动就消失"的真因）。
    // 仅当设备列表数据真正变化时才重建，让菜单在轮询期间保持打开。
    private string _deviceListSignature = "";

    private void SyncMultiDeviceList()
    {
        var all = _pods.State.ConnectedDevices.ToList();
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;
        var canManage = caps.HasMultiConnectManage;

        // 无设备且已连接时，用型号名填充
        if (all.Count == 0 && _pods.State.Connected)
        {
            all.Add(new ConnectedDeviceInfo
            {
                Address = "current",
                DeviceName = caps.ModelName,
                ConnectionState = 2,
                IsCurrentDevice = true,
            });
        }

        // 计算列表签名（含影响渲染与菜单的所有字段 + 能力 + 连接态）
        var sig = string.Join("|", all.Select(d =>
            $"{d.Address};{d.DeviceName};{d.ConnectionState};{d.IsCurrentDevice};{d.IsAudioActive};{d.IsMainAudioDevice}"))
            + $"##manage={canManage};auto={_pods.State.MultiConnectAutoMode};conn={_pods.State.Connected}";
        if (sig == _deviceListSignature && DeviceList.Items.Count > 0)
            return;   // 数据未变，跳过重建，保住已打开的菜单
        _deviceListSignature = sig;

        DeviceList.Items.Clear();

        // 自动模式提示
        if (canManage && _pods.State.MultiConnectAutoMode && all.Count > 1)
        {
            var autoHint = new TextBlock
            {
                Text = "（自动切换模式）",
                FontSize = 10,
                Opacity = 0.35,
                Margin = new Thickness(14, 0, 0, 4),
                Foreground = BrushLightGreen,
            };
            DeviceList.Items.Add(autoHint);
        }

        foreach (var d in all)
        {
            // 连接状态圆点
            var dotColor = d.ConnectionState switch { 2 => BrushGreen, 1 => BrushGray, _ => BrushRed };
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Fill = dotColor };

            // 设备名
            var nameTb = new TextBlock { Text = d.DeviceName, FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            if (d.IsCurrentDevice)
                nameTb.Foreground = BrushLightGreen;

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(4, 3) };
            row.Children.Add(dot);
            row.Children.Add(nameTb);

            // 音频活动指示
            if (d.IsAudioActive && !d.IsCurrentDevice)
            {
                var audioHint = new TextBlock { Text = " ♪", FontSize = 11, Opacity = 0.6,
                    Foreground = BrushGreen, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                row.Children.Add(audioHint);
            }
            if (d.IsCurrentDevice)
            {
                var note = new TextBlock { Text = d.IsAudioActive ? "  ♪" : "", FontSize = 11, Opacity = 0.5,
                    Foreground = BrushLightGreen, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                row.Children.Add(note);
            }

            // 连接状态文字
            if (d.ConnectionState != 2 && !d.IsCurrentDevice)
            {
                var status = new TextBlock { Text = $" ({d.ConnectionStatus})", FontSize = 10, Opacity = 0.4,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                row.Children.Add(status);
            }

            var border = new Border { Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(4), Child = row };
            if (d.IsCurrentDevice)
                border.Background = new SolidColorBrush(Color.FromArgb(0x12, 0x4C, 0xAF, 0x50));

            // 右键菜单 / 左键操作
            var menu = new ContextMenu();
            // isReal：有真实 MAC 的列表项（"current" 是无多连接列表时的型号占位，不能下发操作）
            var isReal = !string.IsNullOrEmpty(d.Address) && d.Address != "current";

            if (isReal && !d.IsCurrentDevice)
            {
                if (d.ConnectionState != 2)
                {
                    // 已断开 → 连接
                    var connect = new MenuItem { Header = $"连接「{d.DeviceName}」" };
                    connect.Click += (_, _) => _pods.SendMultiConnectConnect(d.Address);
                    menu.Items.Add(connect);
                }
                else
                {
                    // 已连接 → 切换音频 / 断开
                    if (canManage)
                    {
                        var setPri = new MenuItem { Header = "切换音频到此设备" };
                        setPri.Click += (_, _) => _pods.SendMultiConnectSetPriority(d.Address);
                        menu.Items.Add(setPri);
                        menu.Items.Add(new Separator());
                    }
                    var disconnect = new MenuItem { Header = $"断开「{d.DeviceName}」" };
                    disconnect.Click += (_, _) => _pods.SendMultiConnectDisconnect(d.Address);
                    menu.Items.Add(disconnect);
                }
                // 取消配对（所有非当前设备）
                menu.Items.Add(new Separator());
                var unpair = new MenuItem { Header = "取消配对" };
                unpair.Click += (_, _) => _pods.SendMultiConnectUnpair(d.Address);
                menu.Items.Add(unpair);
            }
            else if (isReal && d.IsCurrentDevice)
            {
                // 当前设备（本机）也可调整：把音频主通道切回本机（当音频正在别的设备上时有用）。
                // 依据 melody：当前设备走的是设为优先设备，不提供断开/解绑（不能把自己踢掉）。
                if (canManage && !d.IsAudioActive)
                {
                    var setPri = new MenuItem { Header = "切换音频到本机" };
                    setPri.Click += (_, _) => _pods.SendMultiConnectSetPriority(d.Address);
                    menu.Items.Add(setPri);
                }
            }

            if (menu.Items.Count > 0)
            {
                border.ContextMenu = menu;
                border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

                // 左键快捷操作：已连接 → 切换音频；已断开 → 连接
                border.PointerPressed += (s, e) =>
                {
                    var pt = e.GetCurrentPoint(border);
                    if (!pt.Properties.IsLeftButtonPressed) return;
                    if (d.IsCurrentDevice)
                    {
                        // 当前设备（本机）：仅在支持管理且音频不在本机时，把音频切回本机
                        if (canManage && !d.IsAudioActive)
                        {
                            Log.D("UI", $"切换音频到本机 -> {d.DeviceName} ({d.Address})");
                            _pods.SendMultiConnectSetPriority(d.Address);
                        }
                    }
                    else if (d.ConnectionState == 2)
                    {
                        if (canManage)
                        {
                            Log.D("UI", $"切换音频 -> {d.DeviceName} ({d.Address})");
                            _pods.SendMultiConnectSetPriority(d.Address);
                        }
                        else
                        {
                            Log.D("UI", $"连接/切换设备 -> {d.DeviceName} ({d.Address})");
                            _pods.SendOperateHandheld(d.Address, true);
                        }
                    }
                    else
                    {
                        Log.D("UI", $"连接设备 -> {d.DeviceName} ({d.Address})");
                        _pods.SendMultiConnectConnect(d.Address);
                    }
                };
            }

            DeviceList.Items.Add(border);
        }

        // 更新状态栏
        var current = all.FirstOrDefault(d => d.IsCurrentDevice);
        StatusText.Text = current != null
            ? $"已连接 — {current.DeviceName}"
            : (_pods.State.Connected ? "已连接" : "未连接");
    }




    private void ShowFromTray()
    {
        // 静默启动时曾关闭任务栏显示，用户主动打开时恢复
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleFromTray()
    {
        if (IsVisible)
            Hide();
        else
            ShowFromTray();
    }

    private void UpdateTrayTooltip(string text)
    {
        if (_trayIcon != null)
            _trayIcon.ToolTipText = text;
    }

    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon
            {
                Icon = _iconConnected ?? _iconDisconnected,
                ToolTipText = "OPPO Pods · 单击小窗 / 双击主窗口",
                IsVisible = true
            };
            _trayIcon.Clicked += OnTrayClicked;

            var icons = new TrayIcons { _trayIcon };
            if (Application.Current != null)
                TrayIcon.SetIcons(Application.Current, icons);

            RebuildTrayMenu();
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "SetupTrayIcon", ex);
        }
    }

    private void OnTrayClicked(object? s, EventArgs e)
    {
        if (_trayClickTimer == null)
        {
            // 首次点击 → 启动 400ms 定时器
            _trayClickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
                (_, _) =>
                {
                    _trayClickTimer?.Stop();
                    // 超时 → 单击 → 显示小窗
                    ShowSmallWindow();
                });
            _trayClickTimer.Start();
        }
        else
        {
            // 第二次点击 → 双击 → 显示大窗
            _trayClickTimer.Stop();
            _trayClickTimer = null;
            ShowBigWindow();
        }
    }

    private void ShowSmallWindow()
    {
        _trayClickTimer = null;

        if (_smallWindow != null && _smallWindow.IsVisible)
        {
            _smallWindow.Hide();
            return;
        }

        if (_smallWindow == null)
        {
            _smallWindow = new SmallWindow(_pods, () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_smallWindow == null) return;
                    // Show 后 300ms 内忽略 Deactivated，避免 Show/Activate 过程中瞬时焦点丢失导致循环
                    if ((DateTime.Now - _smallWindowShownAt).TotalMilliseconds < 300) return;
                    try { _smallWindow.Hide(); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                    _trayClickTimer = null;
                });
            });
            // 窗口被用户点 X 关闭后置 null，下次重新创建（Close 后不能再 Show）
            _smallWindow.Closed += (_, _) => _smallWindow = null;
        }

        // 先 Show 再定位：Show 后 FrameSize 才有值，能拿到含装饰的实际窗口尺寸
        _smallWindow.Show();
        _smallWindow.Activate();
        _smallWindowShownAt = DateTime.Now;

        // 定位到屏幕右下角（紧贴任务栏上方）
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            // FrameSize 含窗口装饰（阴影/边框），比 Width/Height 更准确
            var frame = _smallWindow.FrameSize;
            var w = (int)Math.Ceiling((frame?.Width  ?? _smallWindow.Width)  * scale);
            var h = (int)Math.Ceiling((frame?.Height ?? _smallWindow.Height) * scale);
            _smallWindow.Position = new PixelPoint(
                area.X + area.Width  - w,
                area.Y + area.Height - h);
        }
    }

    private void ShowBigWindow()
    {
        _trayClickTimer = null;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>按当前连接状态与设备能力重建托盘右键菜单（NativeMenu 原生菜单）。</summary>
    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;
        var s = _pods.State;
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;
        var menu = new NativeMenu();
        _trayAncMap.Clear();

        if (s.Connected)
        {
            // ANC 模式切换
            if (caps.AncOptions.Count > 0)
            {
                foreach (var opt in caps.AncOptions)
                {
                    if (opt.Children.Count > 0)
                    {
                        foreach (var child in opt.Children)
                        {
                            var active = _ancLevel == child.Key;
                            var item = new NativeMenuItem((active ? "✓ " : "    ") + child.Label);
                            _trayAncMap[item] = (child.Key, opt.Key, true);
                            item.Click += TrayAncItem_Click;
                            menu.Add(item);
                        }
                    }
                    else
                    {
                        var active = _ancMain == opt.Key;
                        var item = new NativeMenuItem((active ? "✓ " : "") + opt.Label);
                        _trayAncMap[item] = (opt.Key, "", false);
                        item.Click += TrayAncItem_Click;
                        menu.Add(item);
                    }
                }
                menu.Add(new NativeMenuItemSeparator());
            }

            // 功能开关
            if (caps.HasGameMode)
            {
                var item = new NativeMenuItem((s.GameMode ? "✓ " : "") + "游戏模式");
                item.Click += (_, _) => { _pods.SendGameMode(!s.GameMode, _gameModeCompat); };
                menu.Add(item);
            }
            if (caps.HasSpatialSound)
            {
                var item = new NativeMenuItem((s.SpatialSound ? "✓ " : "") + "空间音效");
                item.Click += (_, _) => _pods.SendSpatial(!s.SpatialSound);
                menu.Add(item);
            }
            if (caps.HasDualDevice)
            {
                var item = new NativeMenuItem((s.DualDevice ? "✓ " : "") + "双设备连接");
                item.Click += (_, _) => _pods.SendDualDevice(!s.DualDevice);
                menu.Add(item);
            }
            if (caps.HasGameMode || caps.HasSpatialSound || caps.HasDualDevice)
                menu.Add(new NativeMenuItemSeparator());
        }

        var showItem = new NativeMenuItem("显示主页面");
        showItem.Click += (_, _) => ShowFromTray();
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        var quitItem = new NativeMenuItem("退出");
        quitItem.Click += (_, _) => QuitApplication();
        menu.Add(quitItem);
        _trayIcon.Menu = menu;
    }

    /// <summary>托盘 ANC 菜单项点击：从字典查键，避免闭包捕获问题。</summary>
    private void TrayAncItem_Click(object? sender, EventArgs e)
    {
        if (sender is not NativeMenuItem item || !_trayAncMap.TryGetValue(item, out var info))
            return;
        if (!_pods.IsConnected) return;
        _ancUserSetAt = DateTime.Now;
        if (info.isChild)
        {
            _ancMain = info.parentKey;
            _ancLevel = info.key;
        }
        else
        {
            _ancMain = info.key;
            _ancLevel = "";
        }
        _pods.SendAnc(info.key);
        HighlightAnc();
        RebuildTrayMenu();
    }

    private void SyncUi() => Dispatcher.UIThread.Post(() => OnStateChanged());

    private void SpatialAudio_Init()
    {
        // Wire RadioButton IsCheckedChanged events for spatial audio group
        if (SpatialAudioPanel.Child is StackPanel sp)
            foreach (var child in sp.Children)
                if (child is WrapPanel wp)
                    foreach (var c in wp.Children)
                        if (c is RadioButton rb)
                            rb.IsCheckedChanged += SpatialAudio_Changed;
    }

    /// <summary>按设备回读的空间音频三模式（0x812A）静默勾选对应单选项，不触发 SendSpatialAudio。</summary>
    private void SyncSpatialModeFromState(string mode)
    {
        if (SpatialAudioPanel.Child is not StackPanel sp) return;
        foreach (var child in sp.Children)
            if (child is WrapPanel wp)
                foreach (var c in wp.Children)
                    if (c is RadioButton rb && rb.Tag is string tag)
                    {
                        bool shouldCheck = tag == mode;
                        if (rb.IsChecked == shouldCheck) continue;
                        // 临时摘除事件，避免回读同步反向触发设置命令
                        rb.IsCheckedChanged -= SpatialAudio_Changed;
                        rb.IsChecked = shouldCheck;
                        rb.IsCheckedChanged += SpatialAudio_Changed;
                    }
    }

    // ========== 品牌/系列/机型树构建 ==========

    private static Dictionary<string, Dictionary<string, List<string>>> BuildBrandTree(List<string> allNames)
    {
        var tree = new Dictionary<string, Dictionary<string, List<string>>>();
        var brandKeywords = new Dictionary<string, string[]>
        {
            ["OPPO"] = new[] { "OPPO", "Enco" },
            ["OnePlus"] = new[] { "OnePlus", "Buds" },
            ["realme"] = new[] { "realme", "Buds" },
        };

        foreach (var name in allNames)
        {
            string brand = "其他";
            foreach (var (b, keywords) in brandKeywords)
                if (keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                { brand = b; break; }

            if (!tree.ContainsKey(brand))
                tree[brand] = new Dictionary<string, List<string>>();

            var series = ExtractSeries(name);
            if (!tree[brand].ContainsKey(series))
                tree[brand][series] = new List<string>();
            tree[brand][series].Add(name);
        }
        return tree;
    }

    private static string ExtractSeries(string modelName)
    {
        // 从型号名提取子系列名（型号名中的第一个单词/数字+字母组合）
        var parts = modelName.Split(' ', '-', '(', '（');
        return parts.Length > 0 ? parts[0] : modelName;
    }

    private static (string? brand, string? series) FindBrandSeries(string modelName, Dictionary<string, Dictionary<string, List<string>>> tree)
    {
        foreach (var (brand, series) in tree)
            foreach (var (sn, models) in series)
                if (models.Contains(modelName))
                    return (brand, sn);
        return (null, null);
    }

    // ===== 版本更新检查 =====

    private const string UPDATE_API = "https://oppopods.zhaoyi.fun/api/update/latest";
    // private const string UPDATE_API = "http://localhost:57824/api/update/latest";
    private const string DOWNLOAD_URL = "https://github.com/Zhaoyi-ya/OppoPodsManager/releases/latest";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private async void BtnCheckUpdate_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        BtnCheckUpdate.Content = "检查中...";
        try { await DoCheckUpdateAsync(silent: false); }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = "检查更新";
        }
    }

    private async void BtnTestBattery_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var state = new PodState();
        state.Battery["L"] = (12, false);
        state.Battery["R"] = (15, false);
        state.Battery["C"] = (30, false);
        // 模拟首次连接：单次弹出电量弹窗
        await ToastWindow.ShowAsync(state, GetDeviceDisplayName(), ToastType.Battery, GetToastDuration());
    }

    private async void BtnTestToast_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = GetDeviceDisplayName();
        // 构造假电量数据供测试
        var state = new PodState();
        state.Battery["L"] = (85, false);
        state.Battery["R"] = (72, true);
        state.Battery["C"] = (90, false);

        // 依次展示 4 种 Toast 类型，每次间隔 2 秒
        await ToastWindow.ShowAsync(state, name, ToastType.Battery, 2000);
        await Task.Delay(500);
        await ToastWindow.ShowAsync(state, name, ToastType.LowBattery, 2000);
        await Task.Delay(500);
        await ToastWindow.ShowAsync(state, name, ToastType.CriticalBattery, 2000);
        await Task.Delay(500);
        await ToastWindow.ShowAsync(null, name, ToastType.Disconnected, 2000);
    }

    private void BtnViewLog_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshLogView();
        SettingsPanel.IsVisible = false;
        LogPanel.IsVisible = true;

        // 首次打开时获取并监听 ListBox 内部 ScrollViewer 的滚动事件
        if (_logScrollViewer == null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _logScrollViewer = FindScrollViewer(LbLog);
                if (_logScrollViewer != null)
                {
                    _logScrollViewer.ScrollChanged += (_, _) =>
                    {
                        var sv = _logScrollViewer;
                        var atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 1;
                        _logAutoScroll = atBottom;
                    };
                }
            }, DispatcherPriority.Loaded);
        }

        // 启动日志实时刷新定时器
        _logRefreshTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshLogView());
        _logRefreshTimer.Start();
    }

    /// <summary>递归遍历 Visual 树寻找第一个 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(Visual visual)
    {
        if (visual is ScrollViewer sv) return sv;
        foreach (var child in visual.GetVisualChildren())
        {
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }

    private void BtnLogBack_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logRefreshTimer?.Stop();
        _logAutoScroll = true;
        LogPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }

    private async void BtnLogExport_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (top == null) return;
        var storage = top.StorageProvider;
        if (storage == null || !storage.CanSave) return;

        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "导出日志 ZIP",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            SuggestedFileName = $"OPPOPods_logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        });
        if (file == null) return;

        try
        {
            _logManager.ExportLogsAsZip(file.Path.LocalPath);
            await ShowCheckResultDialog($"日志已导出：{file.Path.LocalPath}", "导出成功");
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "BtnLogExport_Click", ex);
            await ShowCheckResultDialog($"导出失败：{ex.Message}", "错误");
        }
    }

    private void BtnLogToggle_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logSimplified = !_logSimplified;
        BtnLogToggle.Content = _logSimplified ? "简化版" : "完整版";
        RefreshLogView();
    }

    /// <summary>将技术日志翻译为白话中文，用于 UI 面板显示。</summary>
    private static string TranslateLog(string raw)
    {
        var ts = raw.AsSpan(0, Math.Min(12, raw.Length)).Trim().ToString();
        foreach (var (pattern, desc) in _logTranslations)
        {
            if (!raw.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;
            var extra = ExtractValue(raw);
            var text = extra.Length > 0 ? $"{desc}：{extra}" : desc;
            return $"{ts}  {text}";
        }
        return "";
    }

    /// <summary>从原始日志行中提取关键数据。</summary>
    private static string ExtractValue(string raw)
    {
        // ParseAnc / ParseNoiseChange: 模式名
        var ancMatch = System.Text.RegularExpressions.Regex.Match(raw, @"(?:ParseAnc|ParseNoiseChange).*?->\s*(\S+)");
        if (ancMatch.Success)
            return ancMatch.Groups[1].Value;

        // ParseWearingData: L='xx' R='xx'
        var wearMatch = System.Text.RegularExpressions.Regex.Match(raw, @"L='([^']+)'\s*R='([^']+)'");
        if (wearMatch.Success)
            return $"左{wearingMap.GetValueOrDefault(wearMatch.Groups[1].Value, wearMatch.Groups[1].Value)} 右{wearingMap.GetValueOrDefault(wearMatch.Groups[2].Value, wearMatch.Groups[2].Value)}";

        // 固件版本/编解码器: =xxx
        var fwMatch = System.Text.RegularExpressions.Regex.Match(raw, @"(?:固件版本|编解码器)=(\S+)");
        if (fwMatch.Success) return fwMatch.Groups[1].Value;

        // 空间音频三模式=xx
        var spaMatch = System.Text.RegularExpressions.Regex.Match(raw, @"空间音频三模式=(\S+)");
        if (spaMatch.Success) return spaMatch.Groups[1].Value;

        // 用户操作: XXX -> value
        var uaMatch = System.Text.RegularExpressions.Regex.Match(raw, @"用户操作.*->\s*(.+)");
        if (uaMatch.Success) return uaMatch.Groups[1].Value.Trim();

        // EQ面板: 切换预设 -> name
        var eqSwMatch = System.Text.RegularExpressions.Regex.Match(raw, @"切换预设\s*->\s*(.+)");
        if (eqSwMatch.Success) return eqSwMatch.Groups[1].Value.Trim();

        // SendEq name=xxx
        var eqNameMatch = System.Text.RegularExpressions.Regex.Match(raw, @"SendEq name=(\S+)");
        if (eqNameMatch.Success) return eqNameMatch.Groups[1].Value;

        // DeleteEq eqId=N
        var delEqMatch = System.Text.RegularExpressions.Regex.Match(raw, @"DeleteEq eqId=(\d+)");
        if (delEqMatch.Success) return $"ID={delEqMatch.Groups[1].Value}";

        // 多设备操作: 操作 addr=XX
        var multiMatch = System.Text.RegularExpressions.Regex.Match(raw, @"多设备操作:\s*(\S+)\s+addr=(\S+)");
        if (multiMatch.Success)
            return $"{multiMatch.Groups[1].Value} {multiMatch.Groups[2].Value}";

        // 连接/切换设备 -> xxx
        var devMatch = System.Text.RegularExpressions.Regex.Match(raw, @"(?:连接|切换)设备\s*->\s*(.+)");
        if (devMatch.Success) return devMatch.Groups[1].Value.Trim();

        // 命令超时 cmd=0xXXXX
        var toMatch = System.Text.RegularExpressions.Regex.Match(raw, @"cmd=0x([0-9A-Fa-f]+)");
        if (toMatch.Success && raw.Contains("超时"))
            return $"0x{toMatch.Groups[1].Value}";

        // Connect: OK — name="xxx" / Locate: 命中 name="xxx"
        var nameMatch = System.Text.RegularExpressions.Regex.Match(raw, """name="([^"]+)""");
        if (nameMatch.Success) return nameMatch.Groups[1].Value;

        // 精确识别为 xxx
        var idMatch = System.Text.RegularExpressions.Regex.Match(raw, "精确识别为 (.+)");
        if (idMatch.Success) return idMatch.Groups[1].Value.Trim();

        // ParseEqAll / ParseMultiConnect: N 个预设/设备
        var cntMatch = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+)\s*个(?:预设|设备)");
        if (cntMatch.Success) return $"{cntMatch.Groups[1].Value}个";

        // 枚举到 N 个候选
        var enumMatch = System.Text.RegularExpressions.Regex.Match(raw, @"枚举到\s*(\d+)\s*个");
        if (enumMatch.Success) return $"{enumMatch.Groups[1].Value}个";

        // 失败信息
        var failMatch = System.Text.RegularExpressions.Regex.Match(raw, "失败 -> (.+)");
        if (failMatch.Success) return failMatch.Groups[1].Value.Trim();

        // 重试剩余次数
        var retryMatch = System.Text.RegularExpressions.Regex.Match(raw, @"剩余重试\s*(\d+)");
        if (retryMatch.Success) return $"剩余{retryMatch.Groups[1].Value}次";

        // 拦截不支持的命令 → 型号名
        var blockMatch = System.Text.RegularExpressions.Regex.Match(raw, @"当前型号\s+(\S+)\s+无此能力");
        if (blockMatch.Success) return $"{blockMatch.Groups[1].Value}不支持";

        return "";
    }

    private static readonly Dictionary<string, string> wearingMap = new()
    {
        ["佩戴"] = "佩戴", ["摘下"] = "摘下", ["入盒"] = "入盒", ["未知"] = "未知"
    };

    private static readonly (string pattern, string desc)[] _logTranslations =
    {
        // === 连接流程 ===
        ("FACTORY", "选择蓝牙传输方式"),
        ("ConnectAsync: 尝试连接", "开始连接耳机"),
        ("Connect: 开始", "正在扫描蓝牙设备"),
        ("RfcommFinder: 枚举到", "扫描到蓝牙服务"),
        ("RfcommFinder: 命中", "找到匹配的耳机服务"),
        ("Connect: 命中服务", "已匹配到蓝牙服务"),
        ("StreamSocket 就绪", "蓝牙数据链路已建立"),
        ("Connect: OK", "蓝牙连接成功"),
        ("已连接,进入轮询", "连接完成，开始后台同步"),
        ("初始化完成", "设备初始化已完成"),
        ("握手命令已发完", "正在等待设备响应"),
        ("开始握手序列", "开始与耳机交换信息"),
        // === 设备识别 ===
        ("精确识别为", "自动识别型号："),
        ("productId=", "读取设备型号代码"),
        ("名称预判 Caps=", "预判设备型号："),
        // === 固件与电池 ===
        ("固件版本=", "固件版本"),
        ("编解码器=", "音频编解码器"),
        ("Send cmd=0x0106", "查询电量"),
        ("Send cmd=0x010D", "查询电池详情"),
        ("ParseBattery", "电池数据"),
        // === ANC 降噪 ===
        ("Send cmd=0x010C", "查询降噪"),
        ("ParseAnc:", "当前降噪模式"),
        ("ParseNoiseChange", "降噪模式变更"),
        // === EQ 调音 ===
        ("ParseEqAll:", "预设列表同步"),
        ("SendQueryEqAll", "查询所有预设"),
        ("SendEq name=", "切换调音预设"),
        ("SendCustomEq", "设置自定义均衡器"),
        ("DeleteEq eqId", "删除自定义预设"),
        ("Send cmd=0x0122", "查询预设数据"),
        ("收到 0x8122", "预设数据"),
        // === 空间音频 ===
        ("空间音频三模式", "空间音频模式"),
        // === 多设备 ===
        ("ParseMultiConnect: 列表更新", "多设备列表"),
        ("Send cmd=0x0112", "查询多设备"),
        // === 用户操作 ===
        ("用户操作: ANC", "降噪切换"),
        ("用户操作: EQ", "调音切换"),
        ("用户操作: 空间音频", "空间音频切换"),
        ("用户操作: 空间声场", "空间声场开关"),
        ("用户操作: 游戏模式开关", "游戏模式"),
        ("用户操作: 游戏音效开关", "游戏音效"),
        ("用户操作: 双设备开关", "双设备连接"),
        ("用户操作: 切换主题 ->", "切换主题"),
        ("EQ面板: 切换预设", "EQ面板：切换预设"),
        ("EQ保存:", "EQ面板：保存预设"),
        // === 通知与状态 ===
        ("注册通知完成", "通知注册成功"),
        ("ParseActiveReport: subType=0x02", "佩戴状态"),
        ("ParseActiveReport: subType=0x06", "多设备状态"),
        ("ParseActiveReport: 多连接状态变更", "多设备状态变化"),
        ("Send cmd=0x0205", "注册通知"),
        ("ParseWearingData", "佩戴状态"),
        ("Send cmd=0x010F", "查询功能开关"),
        ("Send cmd=0x0105", "查询固件"),
        ("Send cmd=0x0114", "查询空间音效"),
        // === 能力门控 ===
        ("拦截不支持的命令", "功能不支持"),
        // === 配置 ===
        ("CFG] Load:", "读取配置"),
        ("CFG] Save:", "保存配置"),
        // === 命令分发 ===
        ("命令超时", "命令超时"),
        ("重发超时命令", "命令重试"),
        ("设置失败", "命令失败"),
        ("设置成功", "命令成功"),
        ("重连", "重新连接"),
        ("5s 后重试", "5秒后重试连接"),
        // === UI 事件 ===
        ("InitializeComponent OK", "界面加载完成"),
        ("MainWindow 构造开始", "应用启动"),
        // === 设备发现 ===
        ("Locate: 命中", "发现设备"),
        ("Locate: 按名称命中", "按名称找到设备"),
        ("Locate: 按 SPP UUID 命中", "按蓝牙服务找到设备"),
        // === 多设备操作 ===
        ("多设备操作:", "多设备操作"),
        ("连接/切换设备", "连接设备"),
        ("连接设备 ->", "连接设备"),
        // === 异常 ===
        ("EXCEPTION", "发生异常"),
    };

    private async Task CheckForUpdateAsync()
    {
        await Task.Delay(5000);
        if (SettingsManager.GetString("AutoCheckUpdate") == "false") return;
        await DoCheckUpdateAsync(silent: true);
    }

    private async Task DoCheckUpdateAsync(bool silent = false)
    {
        try
        {
            var resp = await _http.GetStringAsync(UPDATE_API);
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var json = doc.RootElement;
            var serverVersion = json.GetProperty("version").GetString();
            var content = json.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var downloadUrl = json.TryGetProperty("download_url", out var u) ? u.GetString() ?? DOWNLOAD_URL : DOWNLOAD_URL;

            if (string.IsNullOrEmpty(serverVersion) || !IsNewerThan(serverVersion, VersionText.Text!))
            {
            if (!silent) await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog($"已是最新版本 ({VersionText.Text})"));
            return;
            }

            // 自动检查时才跳过已跳过的版本
            if (silent)
            {
                var skipped = SettingsManager.GetString("SkippedVersion");
                if (serverVersion == skipped) return;
            }

            var go = await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowUpdateDialog(serverVersion, content));
            if (go)
                Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
        }
        catch
        {
            if (!silent) await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog("检查更新失败，请检查网络连接"));
        }
    }

    private async Task ShowCheckResultDialog(string msg, string title = "检查更新")
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = msg;
        DialogInput.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogCancelBtn.IsVisible = false;
        DialogConfirmBtn.Content = "确定";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        await _confirmTcs.Task;
    }

    /// <summary>比较版本号：server &gt; local 返回 true。支持 v1.0.6 &gt; v1.0.5。</summary>
    private static bool IsNewerThan(string server, string local)
    {
        // 去掉 v/V 前缀
        var sv = server.StartsWith('v') || server.StartsWith('V') ? server[1..] : server;
        var lv = local.StartsWith('v') || local.StartsWith('V') ? local[1..] : local;

        if (System.Version.TryParse(sv, out var sVer) && System.Version.TryParse(lv, out var lVer))
            return sVer > lVer;

        // 非标准格式（如 v1.0.6beta）回退到字符串比较，抛日志提醒
        Log.D("UPDATE", $"非标准版本号格式: server={server} local={local}");
        return string.Compare(sv, lv, StringComparison.Ordinal) > 0;
    }

    private async Task<bool> ShowUpdateDialog(string newVersion, string content = "")
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "发现新版本";
        if (string.IsNullOrEmpty(content))
            DialogMessage.Text = $"新版本 {newVersion} 已发布，当前版本 {VersionText.Text}，是否前往下载？";
        else
            DialogMessage.Text = $"v{newVersion} 已发布\n当前版本：{VersionText.Text}\n\n{content}";
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = "下次提醒我";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogSkipBtn.Content = "跳过此版本";
        DialogSkipBtn.Background = Brushes.Transparent;
        DialogSkipBtn.IsVisible = true;
        DialogConfirmBtn.Content = "前往下载";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        _updatePendingVersion = newVersion;

        return await _confirmTcs.Task;
    }
}

/// <summary>捕获 Trace.WriteLine 输出（含 Log.D/Ex/Result），转发到 LogManager。</summary>
internal sealed class LogTraceListener : TraceListener
{
    private readonly Util.LogManager _logManager;
    public LogTraceListener(Util.LogManager mgr) => _logManager = mgr;
    public override void Write(string? message) { }
    public override void WriteLine(string? message)
    {
        if (message != null)
            _logManager.AppendLog("TRACE", message);
    }
}
