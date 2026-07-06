using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
    // 前端只依赖 IPodManager 契约；构造点耦合具体类，其余交互全走接口
    private readonly IPodManager _pods = new PodManager();
    private CancellationTokenSource? _pollCts;
    private string _ancMain = "", _ancLevel = "";
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
    private DispatcherTimer? _trayClickTimer;
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

        // EQ 滑块事件
        EqSlider62.PropertyChanged += EqSlider_Changed;
        EqSlider250.PropertyChanged += EqSlider_Changed;
        EqSlider1k.PropertyChanged += EqSlider_Changed;
        EqSlider4k.PropertyChanged += EqSlider_Changed;
        EqSlider8k.PropertyChanged += EqSlider_Changed;
        EqSlider16k.PropertyChanged += EqSlider_Changed;
        // EQ 预设列表
        LbEqPresets.SelectionChanged += LbEqPresets_SelectionChanged;

        // 设备详情 — 音效增强互斥
        DiEnhanceNone.IsCheckedChanged += DiEnhance_Changed;
        DiEnhanceSpatial.IsCheckedChanged += DiEnhance_Changed;
        DiEnhanceGame.IsCheckedChanged += DiEnhance_Changed;
        DiEnhanceEq.IsCheckedChanged += DiEnhance_Changed;

        // 初始加载自定义 EQ 预设列表
        RefreshEqPresetList();

        // 多设备下拉
        // 多设备下拉
        SyncMultiDeviceList();

        // 主题：默认跟随系统
        var themeIndex = SettingsManager.GetInt("Theme", 0);
        ApplyTheme(themeIndex);
        CbTheme.SelectedIndex = themeIndex;

        // 开机自启时最小化启动
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized"))
        {
            WindowState = WindowState.Minimized;
            Hide();
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

        _pods.StateChanged += OnStateChanged;
        _pods.StateChanged += () => Dispatcher.UIThread.Post(() => SyncMultiDeviceList());
        Closing += OnWindowClosing;
        Closed += (_, _) => { _pollCts?.Cancel(); _pods.Dispose(); };
        _ = ConnectAsync();
        _ = CheckForUpdateAsync(); // 后台检查更新
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "MainWindow 构造", ex);
            throw;
        }
    }

    private async Task ConnectAsync()
    {
        while (!_realClose)
        {
            Log.D("UI", "ConnectAsync: 尝试连接");
            await _pods.ConnectAsync();
            if (_pods.IsConnected)
            {
                Log.D("UI", "ConnectAsync: 已连接,进入轮询");
                _pollCts = new CancellationTokenSource();
                await _pods.PollAsync(_pollCts.Token);
                Log.D("UI", "ConnectAsync: 轮询结束");
            }
            else
            {
                Log.D("UI", "ConnectAsync: 连接失败 -> " + (_pods.LastError ?? "unknown"));
            }
            _ = Dispatcher.UIThread.InvokeAsync(() => OnStateChanged());
            if (!_realClose) { Log.D("UI", "ConnectAsync: 5s 后重试"); await Task.Delay(5000); }
        }
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
                _ = ToastWindow.ShowAsync(s, caps.ModelName);
                _pods.SendQueryEqAll();  // 首次连接时查询设备端 EQ 列表
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
                _ = ToastWindow.ShowDisconnectedAsync(caps.ModelName);

            ResetUi();
            RebuildTrayMenu();
            return;
        }

        // 检测是否需要刷新 EQ 列表（连接后预设可能变化）
        bool needRebuildEq = CbEq.ItemCount == 0;
        if (!needRebuildEq)
        {
            foreach (var kv in caps.EqPresets)
                if (!CbEq.Items.Contains(kv.Key)) { needRebuildEq = true; break; }
            if (!needRebuildEq)
                foreach (var e in _pods.State.DeviceEqEntries)
                    if (!CbEq.Items.Contains(e.Name)) { needRebuildEq = true; break; }
            // 检测设备端条目是否减少（删除）
            if (!needRebuildEq && _pods.State.DeviceEqEntries.Count != _lastDeviceEqCount)
                needRebuildEq = true;
        }
        _lastDeviceEqCount = _pods.State.DeviceEqEntries.Count;
        if (needRebuildEq)
        {
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.Items.Clear();
            foreach (var kv in caps.EqPresets) CbEq.Items.Add(kv.Key);
            foreach (var e in _pods.State.DeviceEqEntries)
            {
                if (!string.IsNullOrEmpty(e.Name) && !CbEq.Items.Contains(e.Name))
                    CbEq.Items.Add(e.Name);
            }
            CbEq.SelectionChanged += CbEq_SelectionChanged;
            // 同步刷新 EQ 面板左侧列表（仅设备端条目增删时触发）
            RefreshEqPresetList();
        }

        var batL = MergeCharge(s.Battery.GetValueOrDefault("L"), s.WearingL);
        var batR = MergeCharge(s.Battery.GetValueOrDefault("R"), s.WearingR);
        SetBatLabel(LeftLabel, batL);
        SetBatLabel(RightLabel, batR);
        SetBatLabel(CaseLabel, s.Battery.GetValueOrDefault("C"));
        ChargeIndicator.IsVisible = s.Battery.Values.Any(v => v?.Charging == true);

        if (!_lowBatteryAlerted)
        {
            if ((batL is { } l && l.Lvl <= 20) || (batR is { } r && r.Lvl <= 20))
            {
                _lowBatteryAlerted = true;
                _ = ToastWindow.ShowLowBatteryAsync(s, caps.ModelName);
            }
        }

        if (!_criticalBatteryAlerted)
        {
            if ((batL is { } l && l.Lvl <= 10) || (batR is { } r && r.Lvl <= 10))
            {
                _criticalBatteryAlerted = true;
                _ = ToastWindow.ShowCriticalBatteryAsync(s, caps.ModelName);
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

        if (s.EqPreset != "?" && CbEq.SelectedItem == null) CbEq.SelectedItem = s.EqPreset;
        if ((DateTime.Now - _featureUserSetAt).TotalSeconds > 3)
        {
            // 状态回读同步：均用静默设置，避免初始化/轮询勾选反向触发 _Changed 下发命令
            if (s.SpatialSound != _prevSpatialSound) { _prevSpatialSound = s.SpatialSound; SetSpatialCheckedSilent(s.SpatialSound); }
            if (caps.HasSpatialAudio && s.SpatialMode != _prevSpatialMode) { _prevSpatialMode = s.SpatialMode; SyncSpatialModeFromState(s.SpatialMode); }
            if (s.GameMode != _prevGameMode) { _prevGameMode = s.GameMode; SetGameCheckedSilent(s.GameMode); }
            if (s.GameSound != _prevGameSound) { _prevGameSound = s.GameSound; SetGameSoundCheckedSilent(s.GameSound); }
            if (s.DualDevice != _prevDualDevice) { _prevDualDevice = s.DualDevice; SetDualDeviceCheckedSilent(s.DualDevice); }
        }

        if (DeviceInfoPanel.IsVisible) RefreshDeviceInfo();
        if (EqPanel.IsVisible && LbEqPresets.ItemCount == 0) RefreshEqPresetList();

        BuildAncUi(caps);
        AncPanel.IsVisible = caps.AncOptions.Count > 0;
        SpatialAudioPanel.IsVisible = caps.HasSpatialAudio;
        CbSpatial.IsVisible = caps.HasSpatialSound;
        CbDualDevice.IsVisible = caps.HasDualDevice;
        CbGame.IsVisible = caps.HasGameMode;
        CbGameSound.IsVisible = caps.HasGameSound;

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
        try
        {
            using var runKey = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey is null) return;

            if (CbAuto.IsChecked == true)
            {
                SettingsManager.SetBool("AutoStart", true);
                var exe = Environment.ProcessPath ?? "";
                runKey.SetValue("OPPOPods", $"\"{exe}\" --minimized");
            }
            else
            {
                SettingsManager.SetBool("AutoStart", false);
                try { runKey.DeleteValue("OPPOPods", throwOnMissingValue: false); } catch { }
            }
        }
        catch { }
    }
    private void CbAutoUpdate_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        => SettingsManager.SetString("AutoCheckUpdate", CbAutoUpdate.IsChecked == true ? "true" : "false");

    /// <summary>按当前型号 caps.AncOptions 动态生成主/子模式圆形图标按钮（型号不变则跳过重建）。</summary>
    private void BuildAncUi(DeviceCapabilities caps)
    {
        var modelKey = caps.ModelId + "|" + caps.ModelName;
        if (_ancBuiltForModel == modelKey) return;
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
            // 容器型：展开子模式，默认选第一个（或保留上次子级别）
            PopulateAncSub(opt);
            AncSubRow.IsVisible = true;
            var target = opt.Children.Any(c => c.Key == _ancLevel) ? _ancLevel : opt.Children[0].Key;
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
            }
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
            // 音效增强互斥：开游戏音效 → 需显式关空间音效（设备不会自动关），UI 同步取消勾选并下发关闭命令
            if (on && _pods.Caps.GameSoundMutexSpatial && CbSpatial.IsChecked == true)
            {
                SetSpatialCheckedSilent(false);
                _pods.SendSpatial(false);
            }
            _pods.SendGameSound(on);
        }
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
            _pods.SendEq(name);
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

        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.Items.Clear();
        foreach (var kv in caps.EqPresets) CbEq.Items.Add(kv.Key);
        CbEq.SelectionChanged += CbEq_SelectionChanged;

        // 同步刷新 EQ 面板预设列表
        RefreshEqPresetList();

        BuildAncUi(caps);
        AncPanel.IsVisible = caps.AncOptions.Count > 0;
        SpatialAudioPanel.IsVisible = caps.HasSpatialAudio;
        CbSpatial.IsVisible = caps.HasSpatialSound;
        CbDualDevice.IsVisible = caps.HasDualDevice;
        CbGame.IsVisible = caps.HasGameMode;
        CbGameSound.IsVisible = caps.HasGameSound;

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
                _isLightTheme = false; // follow system default (for now treat as dark)
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
        // 浅色模式 SukiUI 资源覆盖
        if (_isLightTheme)
        {
            Resources["SukiBackground"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA));
            Resources["SukiCardBackground"] = new SolidColorBrush(Colors.White);
        }

        // 窗口背景：浅色微灰，深色透明
        Background = _isLightTheme
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xEA))
            : BrushTransparent;

        // 深色毛玻璃(10%白) / 浅色纯白
        var glassBg = _isLightTheme
            ? (IBrush)new SolidColorBrush(Colors.White)
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        // 侧边栏：浅色纯白 / 深色毛玻璃
        var sidebarBg = _isLightTheme
            ? (IBrush)new SolidColorBrush(Colors.White)
            : (IBrush)new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        UpdateGlassCards(MainPanel, glassBg);
        UpdateGlassCards(EqPanel, glassBg);
        UpdateGlassCards(DeviceInfoPanel, glassBg);
        UpdateGlassCards(SettingsPanel, glassBg);
        UpdateGlassCards(AboutPanel, glassBg);

        // 侧边栏底框
        SidebarBorder.Background = sidebarBg;

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

    private void UpdateTitle()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;
        var custom = (TbCustomName.Text ?? "").Trim();
        var name = !string.IsNullOrEmpty(custom) ? custom : caps.ModelName;
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
    private void NavSettings_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("settings");

    private void ShowPage(string page)
    {
        MainPanel.IsVisible = page == "home";
        EqPanel.IsVisible = page == "eq";
        if (page == "eq" && _pods.IsConnected) _pods.SendQueryEqAll();
        DeviceInfoPanel.IsVisible = page == "deviceinfo";
        SettingsPanel.IsVisible = page == "settings";
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
    }

    private void About_Click(object? s, RoutedEventArgs e) => ShowPage("about");
    private void AboutBack_Click(object? s, RoutedEventArgs e) => ShowPage("settings");

    private void OpenUrl_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ========== EQ 调节 ==========

    private string _eqCurrentPreset = "";
    private bool _eqSuppressListEvent;
    private int _lastDeviceEqCount = -1; // 检测 DeviceEqEntries 增删

    /// <summary>滑块值变更 → 更新对应 dB 标签。</summary>
    private void EqSlider_Changed(object? s, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;
        if (s is not Slider slider) return;

        var db = (int)Math.Round(slider.Value);
        var sign = db > 0 ? "+" : "";
        var text = $"{sign}{db}";

        if (slider == EqSlider62) EqDb62.Text = text;
        else if (slider == EqSlider250) EqDb250.Text = text;
        else if (slider == EqSlider1k) EqDb1k.Text = text;
        else if (slider == EqSlider4k) EqDb4k.Text = text;
        else if (slider == EqSlider8k) EqDb8k.Text = text;
        else if (slider == EqSlider16k) EqDb16k.Text = text;
    }

    // ---- 预设列表 ----

    /// <summary>重新加载预设列表（内置置顶 + 自定义）。</summary>
    private void RefreshEqPresetList()
    {
        _eqSuppressListEvent = true;
        LbEqPresets.SelectionChanged -= LbEqPresets_SelectionChanged;
        LbEqPresets.Items.Clear();

        // 内置预设置顶
        foreach (var kv in _pods.Caps.EqPresets)
            LbEqPresets.Items.Add(new EqPresetItem { Name = kv.Key, IsCustom = false });

        // 设备端预设（0x8122 回读）
        foreach (var e in _pods.State.DeviceEqEntries)
        {
            if (!string.IsNullOrEmpty(e.Name) && !_pods.Caps.EqPresets.ContainsKey(e.Name))
                LbEqPresets.Items.Add(new EqPresetItem { Name = e.Name, IsCustom = false, EqId = e.EqId });
        }

        LbEqPresets.SelectionChanged += LbEqPresets_SelectionChanged;
        _eqSuppressListEvent = false;
    }

    /// <summary>预设选中 → 内置/设备端发送切换。</summary>
    private void LbEqPresets_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_eqSuppressListEvent) return;
        if (LbEqPresets.SelectedItem is not EqPresetItem item) return;

        _eqCurrentPreset = item.Name;

        SetAllEqSliders(0);
        BtnEqSave.IsEnabled = true;

        if (_pods.IsConnected)
        {
            Log.D("UI", $"EQ面板: 切换预设 -> {item.Name}");
            _pods.SendEq(item.Name);
        }
        EqHintText.Text = $"预设「{item.Name}」— 拖拽滑块可自定义";
    }

    // ---- 辅助 ----

    private bool IsBuiltinPreset(string name) =>
        _pods.Caps.EqPresets.ContainsKey(name);

    private void SetAllEqSliders(double value)
    {
        EqSlider62.Value = value;
        EqSlider250.Value = value;
        EqSlider1k.Value = value;
        EqSlider4k.Value = value;
        EqSlider8k.Value = value;
        EqSlider16k.Value = value;
    }

    /// <summary>将 6 段 UI 滑块值映射到设备频率数组，未对应 UI 的频段填 0。</summary>
    private int[] SliderToGains()
    {
        var freqSliders = new Dictionary<int, double>
        {
            { 62, EqSlider62.Value },
            { 250, EqSlider250.Value },
            { 1000, EqSlider1k.Value },
            { 4000, EqSlider4k.Value },
            { 8000, EqSlider8k.Value },
            { 16000, EqSlider16k.Value },
        };
        var freqs = _pods.Caps.CustomEqFrequencies;
        if (freqs.Length == 0) return [];
        var gains = new int[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
            gains[i] = freqSliders.TryGetValue(freqs[i], out var v) ? (int)Math.Round(v) : 0;
        return gains;
    }

    /// <summary>向设备发送当前 UI 滑块值作为自定义 EQ。</summary>
    private void SendCurrentCustomEq()
    {
        if (!_pods.IsConnected || _pods.Caps.CustomEqFrequencies.Length == 0) return;
        var gains = SliderToGains();
        _pods.SendCustomEq(gains);
    }

    // ---- 按钮操作 ----

    private async void BtnEqSave_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = await ShowPromptDialog("保存 EQ 预设", "");
        if (string.IsNullOrEmpty(name)) return;
        DoSaveEqPreset(name);
    }

    private async void EqListItemDelete_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not string name) return;
        if (!await ShowConfirmDialog("删除预设", $"确定要删除预设「{name}」吗？")) return;

        // 仅设备端预设可删除，内置预设忽略
        var devEntry = _pods.State.DeviceEqEntries.FirstOrDefault(ev => ev.Name == name);
        if (devEntry != null)
        {
            _pods.DeleteEq(devEntry.EqId);
            // DeleteEq 内部会调 SendQueryEqAll，OnStateChanged 会自动刷新列表
        }
        else
        {
            Log.D("UI", $"EQ面板: 内置预设「{name}」不可删除，已忽略");
            return;
        }

        if (_eqCurrentPreset == name)
        {
            _eqCurrentPreset = "";
            SetAllEqSliders(0);
        }
        EqHintText.Text = $"「{name}」已删除";
    }

    private void DoSaveEqPreset(string name)
    {
        _eqCurrentPreset = name;

        // 发送到设备（带预设名称），后续 OnStateChanged 自动刷新列表
        if (_pods.IsConnected && _pods.Caps.CustomEqFrequencies.Length > 0)
        {
            _pods.SendCustomEq(SliderToGains(), name);
        }

        EqHintText.Text = $"已发送预设「{name}」到设备";
    }

    // ---- 设备详情 ----

    /// <summary>刷新设备详情页（固件、编解码器、音效增强）。</summary>
    private void RefreshDeviceInfo()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _pods.Caps;

        DiDeviceName.Text = caps.ModelName;
        DiFirmware.Text = FormatFirmware(_pods.State.FirmwareVersion);
        DiCodec.Text = CodecName(_pods.State.CodecType);

        // 音效增强互斥组
        bool showSpatial = caps.HasSpatialSound;
        bool showGame = caps.HasGameSound;
        bool showEq = caps.EqPresets.Count > 0;
        bool hasMutex = caps.GameSoundMutexes.Count > 0;

        DiEnhanceNone.IsVisible = hasMutex;
        DiEnhanceSpatial.IsVisible = showSpatial && hasMutex;
        DiEnhanceGame.IsVisible = showGame && hasMutex;
        DiEnhanceEq.IsVisible = showEq && hasMutex;
        DiEnhanceHint.Text = hasMutex
            ? "以下音效互斥，同一时间只能启用一个"
            : "当前设备不支持音效互斥";

        // 刷新选中态
        _diEnhanceSuppress = true;
        var current = _pods.CurrentEnhancement();
        DiEnhanceNone.IsChecked = current == AudioEnhancement.None;
        DiEnhanceSpatial.IsChecked = current == AudioEnhancement.SpatialSound;
        DiEnhanceGame.IsChecked = current == AudioEnhancement.GameSound;
        DiEnhanceEq.IsChecked = current == AudioEnhancement.Eq;
        _diEnhanceSuppress = false;
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

    private static string CodecName(int id) => id switch
    {
        0 => "SBC",
        1 => "AAC",
        2 => "LDAC",
        3 => "LHDC",
        4 => "LC3",
        5 => "aptX",
        6 => "aptX HD",
        7 => "aptX Adaptive",
        8 => "SSC (Samsung)",
        -1 => "-",
        _ => $"未知 ({id})"
    };

    private bool _diEnhanceSuppress;
    private void DiEnhance_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_diEnhanceSuppress || !_pods.IsConnected) return;
        if (s is not RadioButton rb || rb.IsChecked != true) return;

        AudioEnhancement mode;
        if (rb == DiEnhanceNone) mode = AudioEnhancement.None;
        else if (rb == DiEnhanceSpatial) mode = AudioEnhancement.SpatialSound;
        else if (rb == DiEnhanceGame) mode = AudioEnhancement.GameSound;
        else if (rb == DiEnhanceEq) mode = AudioEnhancement.Eq;
        else return;

        Log.D("UI", $"音效增强切换 -> {mode}");
        _pods.SetAudioEnhancement(mode);
    }

    // ---- 浮层对话框（Avalonia 原生遮罩，不创建新窗口）----

    private TaskCompletionSource<string?>? _promptTcs;
    private TaskCompletionSource<bool>? _confirmTcs;
    private string _updatePendingVersion = ""; // 当前提示的新版本号，供跳过使用

    /// <summary>浮层命名输入。</summary>
    private async Task<string?> ShowPromptDialog(string title, string defaultText = "")
    {
        _promptTcs = new TaskCompletionSource<string?>();
        _confirmTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = "请输入预设名称：";
        DialogInput.IsVisible = true;
        DialogInput.Text = defaultText;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.Content = "保存";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        DialogInput.Focus();

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
        DialogConfirmBtn.Content = "确认删除";
        DialogConfirmBtn.Background = new SolidColorBrush(Color.Parse("#CCE81123"));
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        return await _confirmTcs.Task;
    }

    private void DialogSkip_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
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

    // ---- 主页"大师调音"联动 ----

    /// <summary>更新主页 CbEq 列表（内置 + 设备端 + 自定义预设）。</summary>
    private void RefreshMainEqCombo()
    {
        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.Items.Clear();

        // 内置预设
        foreach (var kv in _pods.Caps.EqPresets)
            CbEq.Items.Add(kv.Key);

        // 设备端预设
        foreach (var e in _pods.State.DeviceEqEntries)
        {
            if (!string.IsNullOrEmpty(e.Name) && !CbEq.Items.Contains(e.Name))
                CbEq.Items.Add(e.Name);
        }

        CbEq.SelectionChanged += CbEq_SelectionChanged;
    }

    private void Reconnect_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 点击重连");
        _pods.Disconnect();
    }

    private void TestToast_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var state = new PodState();
        state.Battery["L"] = (75, false);
        state.Battery["R"] = (82, true);
        state.Battery["C"] = (60, false);
        _ = ToastWindow.ShowAsync(state, "OPPO Enco X2 (测试)");
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
    }

    private void OnWindowClosing(object? s, WindowClosingEventArgs e)
    {
        if (_realClose || CbTray.IsChecked != true) return;
        e.Cancel = true;
        Hide();
    }

    // ===== 设备列表 =====
    private void SyncMultiDeviceList()
    {
        DeviceList.Items.Clear();

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

            if (menu.Items.Count > 0)
            {
                border.ContextMenu = menu;
                border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

                // 左键快捷操作：已连接 → 切换音频；已断开 → 连接
                border.PointerPressed += (s, e) =>
                {
                    var pt = e.GetCurrentPoint(border);
                    if (!pt.Properties.IsLeftButtonPressed) return;
                    if (d.ConnectionState == 2)
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
                    try { _smallWindow.Hide(); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                    _trayClickTimer = null;
                });
            });
        }

        // Position before Show
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var area = screen.WorkingArea;
            _smallWindow.Position = new PixelPoint(
                area.X + area.Width  - 390,   // 300(内容) + 90(窗框/阴影)
                area.Y + area.Height - 490);  // 380(内容) + 50(标题栏) + 12(底框) + 48(任务栏)
        }
        _smallWindow.Show();
        _smallWindow.Activate();
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
        quitItem.Click += (_, _) => { _realClose = true; Environment.Exit(0); };
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
    private const string DOWNLOAD_URL = "https://github.com/Zhaoyi-ya/OPPO-Pods-For-Windows/releases/latest";
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
                await ShowUpdateDialog(serverVersion));
            if (go)
                Process.Start(new ProcessStartInfo(DOWNLOAD_URL) { UseShellExecute = true });
        }
        catch
        {
            if (!silent) await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog("检查更新失败，请检查网络连接"));
        }
    }

    private async Task ShowCheckResultDialog(string msg)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "检查更新";
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

    private async Task<bool> ShowUpdateDialog(string newVersion)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "发现新版本";
        DialogMessage.Text = $"新版本 {newVersion} 已发布，当前版本 {VersionText.Text}，是否前往下载？";
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

        // 记住版本号供跳过按钮使用
        _updatePendingVersion = newVersion;

        return await _confirmTcs.Task;
    }
}
