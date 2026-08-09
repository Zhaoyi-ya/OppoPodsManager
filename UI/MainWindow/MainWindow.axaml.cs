using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Assets.UserSettings;
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
using SukiUI.Enums;
using SukiUI.Toasts;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Desktop;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control.Oppo.Models;
using NoiseOptionModel = OppoPodsManager.Control.Oppo.Features.NoiseOptionModel;
using MultiDeviceOperation = OppoPodsManager.Control.Oppo.Features.MultiDeviceOperation;
using MultiDeviceDisplayState = OppoPodsManager.Control.Oppo.Features.MultiDeviceDisplayState;
using OppoPodsManager.Control.Updates;
using BackgroundImageManager = OppoPodsManager.Assets.VisualAssets.BackgroundImageManager;
using BackgroundSelectionService = OppoPodsManager.Assets.VisualAssets.BackgroundSelectionService;
using DeviceProfileLoader = OppoPodsManager.Assets.Localization.DeviceProfileLoader;
using EarphoneImageProvider = OppoPodsManager.Assets.VisualAssets.EarphoneImageProvider;
using EarphoneSlot = OppoPodsManager.Assets.VisualAssets.EarphoneSlot;
using AssetHelper = OppoPodsManager.Assets.VisualAssets.AssetHelper;
using AvaloniaControl = Avalonia.Controls.Control;
using Path = Avalonia.Controls.Shapes.Path;
using OppoPodsManager.UI.Toast;
using EqPresetItem = OppoPodsManager.EqPresetItem;

namespace OppoPodsManager.UI.MainWindow;

// 资源路径常量
file static class AppConst
{
    public const string WindowTitle = "OPPO Pods Manager";
    public const string IconConnected = "avares://OppoPodsManager/Assets/VisualAssets/tuopan.ico";
    public const string IconDisconnected = "avares://OppoPodsManager/Assets/VisualAssets/tuopandis.png";
}

// 从嵌入资源加载图标（适用于 Avalonia + AOT）

public partial class MainWindow : SukiWindow
{
    private FrontendState? _frontendState;
    private ControlManager? _controlManager;
    private CommandDispatcher? _commandDispatcher;
    private readonly SettingsStore _uiSettings;
    private IDisposable? _interactiveSurface;

    private sealed class PriorityDeviceOption
    {
        public string Address { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsAutomatic { get; init; }
        public override string ToString() => DisplayName;
    }

    private readonly List<DeviceConnectionOption> _nextDevices = new();
    private bool _suppressEarbudSelection;

    private string _ancMain = "", _ancLevel = "";
    /// <summary>记住每个父模式上次选的子模式（如 降噪→深度），切换回来时恢复。</summary>
    private readonly Dictionary<string, string> _ancLastSub = new();

    // ANC 动态 UI（按 JSON 生成）：键 → (圆形边框, 图标路径, 文字标签)
    private readonly Dictionary<string, (Ellipse bg, Path icon, TextBlock label)> _ancMainButtons = new();
    private readonly Dictionary<string, (Button btn, Border bg)> _ancSubButtons = new();
    private readonly Dictionary<string, string> _ancChildToMain = new();  // 子模式键 → 所属主模式键
    private List<NoiseOptionModel> _ancOptions = new();                   // 当前型号的 ANC 选项
    private string _ancBuiltForModel = "";                               // 已构建 UI 的型号（避免重复重建）
    private string _ancSubSignature = "";
    private ApplicationLog? _logManager;
    private readonly ObservableCollection<string> _renderedLogEntries = new();
    private int _renderedLogVersion = -1;
    private DispatcherTimer? _logRefreshTimer;
    private bool _logAutoScroll = true;
    private bool _logScrollPending;
    private bool _syncingConnectionStrategy;
    private string _priorityOptionsSignature = "";
    private ScrollViewer? _logScrollViewer;
    private DispatcherTimer? _eqDebounceTimer;
    private DispatcherTimer? _bgApplyDebounceTimer;
    private IImage? _backgroundImageSource;
    private bool _realClose;
    private bool _runtimeUiDisposed;
    private bool _sukiWindowDisposed;
    private bool _initializingSettings;
    private string _currentPage = "";
    // 统一管理主窗口和小窗使用的自定义背景资源。
    private readonly BackgroundImageManager _backgroundImages = new();
    private readonly BackgroundSelectionService _backgroundSelection;
    /// <summary>托盘 ANC 菜单项 → (发送键, 父键, 是否子模式)，避免闭包捕获。</summary>
    internal static ISukiToastManager ToastManager = new SukiToastManager();
    private string? _modelOverride;
    private DateTime _connectionStatusStartedAt = DateTime.MinValue;
    private bool _findDeviceActive;
    private bool _wasConnected;
    private bool _applyingAppearancePreset;

    // 缓存画刷
    private static SolidColorBrush BrushGreen { get; } = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static SolidColorBrush BrushRed { get; } = new(Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush BrushTransparent = new SolidColorBrush(Colors.Transparent);
    // 主题自适应：浅色模式用暗色，深色模式用亮色
    private SolidColorBrush BrushGray => _isLightTheme ? _brushGrayLight : _brushGrayDark;
    private SolidColorBrush BrushWhite => _isLightTheme ? _brushDark : _brushWhiteDark;
    private static readonly SolidColorBrush _brushGrayDark = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush _brushGrayLight = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush _brushWhiteDark = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush _brushDark = new(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x60, 0x90, 0xFF));
    private static readonly SolidColorBrush BrushWhitePure = new SolidColorBrush(Colors.White);
    private readonly SolidColorBrush _glassCardBgBrush = new(Colors.White);
    private readonly SolidColorBrush _sidebarSelectedBgBrush = new(Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _dialogOverlayBgBrush = new(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _textPanelButtonBgBrush = new(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _textPanelButtonHoverBgBrush = new(Color.FromArgb(0x12, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _textPanelButtonPressedBgBrush = new(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _windowBackgroundBrush = new(Color.FromRgb(0xE5, 0xE5, 0xEA));
    private readonly SolidColorBrush _sidebarBackgroundBrush = new(Colors.White);
    private readonly SolidColorBrush _deviceCurrentBgBrush = new(Color.FromArgb(0x12, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush BrushBatteryLow = new(Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly SolidColorBrush BrushBatteryMid = new(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly SolidColorBrush BrushBatteryHigh = new(Color.FromRgb(0x4C, 0xD9, 0x64));
    // 复用画刷：状态文字、圆圈边框、圆圈背景、ANC 强调色（浅色主题）
    private readonly SolidColorBrush _brushLightGreenLight = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private readonly SolidColorBrush _brushLightGreenDark = new(Color.FromRgb(0x88, 0xCC, 0x88));
    private readonly SolidColorBrush _brushLightRedLight = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private readonly SolidColorBrush _brushLightRedDark = new(Color.FromRgb(0xFF, 0x88, 0x88));
    private readonly SolidColorBrush _brushCircleStrokeLight = new(Color.FromArgb(0x20, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _brushCircleStrokeDark = new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _brushCircleStrokeInactiveLight = new(Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _brushCircleStrokeInactiveDark = new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _brushCircleGrayLight = new(Color.FromArgb(0x15, 0x00, 0x00, 0x00));
    private readonly SolidColorBrush _brushCircleGrayDark = new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _brushAccentLight = new(Color.FromRgb(0x25, 0x63, 0xEB));
    private SolidColorBrush BrushLightGreen => _isLightTheme ? _brushLightGreenLight : _brushLightGreenDark;
    private SolidColorBrush BrushLightRed => _isLightTheme ? _brushLightRedLight : _brushLightRedDark;
    private SolidColorBrush BrushCircleStroke => _isLightTheme ? _brushCircleStrokeLight : _brushCircleStrokeDark;
    private SolidColorBrush BrushCircleStrokeInactive => _isLightTheme ? _brushCircleStrokeInactiveLight : _brushCircleStrokeInactiveDark;
    private bool _themeResourceBrushesRegistered;
    private bool _isLightTheme;
    private readonly List<IDisposable> _linguaSubs = new();
    private string _statusDisconnected = "";
    private string _statusConnected = "";
    private string _statusIdentifying = "";
    private string _statusUnidentified = "";
    private string _findDevice = "";
    private string _stopFindDevice = "";
    private string _checkUpdate = "";
    private string _checking = "";
    // 复用控制层更新协调器，窗口只负责更新结果的显示和用户操作。
    private readonly UpdateCoordinator? _updateCoordinator;
    private readonly DesktopLinkService? _desktopLinks;
    private readonly FeedbackExportService? _feedbackExporter;
    private readonly Action? _requestApplicationExit;
    private readonly Func<bool>? _shouldKeepWindowAlive;

    private bool _gameSoundCommandPending;

    // 三级联动：品牌 → 子系列 → 机型
    private readonly ObservableCollection<string> _brandList = new();
    private readonly ObservableCollection<string> _seriesList = new();
    private readonly ObservableCollection<string> _modelList = new();
    private readonly ObservableCollection<LanguageOption> _languageList = new();
    private bool _refreshingComboBoxes;
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> _brandTree
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();
    private readonly ModelCatalog? _modelCatalog;

    // 为 Avalonia/AOT 提供无参入口，不创建设备连接层。
    public MainWindow() : this(null)
    {
    }

    private MainWindow(
        OppoPodsManager.Assets.UserSettings.SettingsManager? nextSettings,
        ModelCatalog? modelCatalog = null,
        CommandDispatcher? commandDispatcher = null,
        UpdateCoordinator? updateCoordinator = null,
        DesktopLinkService? desktopLinks = null,
        FeedbackExportService? feedbackExporter = null,
        Action? requestApplicationExit = null,
        Func<bool>? shouldKeepWindowAlive = null)
    {
        _modelCatalog = modelCatalog;
        // 窗口只保存应用层注入的调度器，不在 UI 内部创建控制逻辑。
        _commandDispatcher = commandDispatcher;
        // 更新协调器由应用生命周期注入；AOT 无参构造只负责加载视图资源。
        _updateCoordinator = updateCoordinator;
        _desktopLinks = desktopLinks;
        _feedbackExporter = feedbackExporter;
        _requestApplicationExit = requestApplicationExit;
        _shouldKeepWindowAlive = shouldKeepWindowAlive;
        _uiSettings = new SettingsStore(nextSettings);
        _backgroundSelection = new BackgroundSelectionService(_uiSettings, _backgroundImages);
        _logManager = ApplicationLog.Current;
        try
        {
            _logManager?.Debug("UI", "MainWindow 构造开始");
        InitializeComponent();

        // 初始化降噪卡片为空状态，未连接设备时仍保留卡片容器。
        AncPanel.IsVisible = true;
        ResetNextAncUi();

        // 版本号统一取自 csproj 的 <Version>（经程序集元数据），避免 XAML 硬编码。
        // GetName().Version 在 AOT 下可用（与 DeviceProfileLoader 读取资源同源），
        // 末位 Revision 为 0 时舍去，保持 "v主.次.修订" 三段格式。
        var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = asmVer != null
            ? $"v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
            : "v?";

        AdaptToPlatform();
        AddHandler(PointerPressedEvent, CloseFloatingMenusOnBlankClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        NavHome.Classes.Add("selected");
        LbLog.ItemsSource = _renderedLogEntries;

            _logManager?.Debug("UI", "InitializeComponent OK");

        // Wire events programmatically (Avalonia 12 compatibility)
            CbSpatial.IsCheckedChanged += CbSpatial_Changed;
            CbGame.IsCheckedChanged += CbGame_Changed;
            CbGameSound.IsCheckedChanged += CbGameSound_Changed;
        CbDualDevice.IsCheckedChanged += CbDualDevice_Changed;
        CbBassEngine.IsCheckedChanged += CbBassEngine_Changed;
        CbVocalEnhance.IsCheckedChanged += CbVocalEnhance_Changed;
        CbHearingEnhance.IsCheckedChanged += CbHearingEnhance_Changed;
        CbLongPower.IsCheckedChanged += CbLongPower_Changed;
        CbWearDetection.IsCheckedChanged += CbWearDetection_Changed;
        CbSpineHealth.IsCheckedChanged += CbSpineHealth_Changed;
        CbTray.IsCheckedChanged += CbTray_Changed;
        CbAuto.IsCheckedChanged += CbAuto_Changed;
        CbAutoUpdate.IsCheckedChanged += CbAutoUpdate_Changed;
        CbPriorityDevice.SelectionChanged += CbPriorityDevice_Changed;
        CbEq.SelectionChanged += CbEq_SelectionChanged;
        CbBrand.SelectionChanged += CbBrand_Changed;
        CbSeries.SelectionChanged += CbSeries_Changed;
        CbModel.SelectionChanged += CbModel_Changed;
        CbTheme.SelectionChanged += CbTheme_Changed;
        CbLanguage.SelectionChanged += CbLanguage_Changed;
        CbDevice.SelectionChanged += CbDevice_Changed;
        TbCustomName.TextChanged += TbCustomName_Changed;

        // EQ 滑块事件
        EqSlider62.PropertyChanged += EqSlider_Changed;
        EqSlider250.PropertyChanged += EqSlider_Changed;
        EqSlider1k.PropertyChanged += EqSlider_Changed;
        EqSlider4k.PropertyChanged += EqSlider_Changed;
        EqSlider8k.PropertyChanged += EqSlider_Changed;
        EqSlider16k.PropertyChanged += EqSlider_Changed;
        // EQ 预设列表（左右分栏：系统预设 / 自定义）
        LbEqBuiltinPresets.SelectionChanged += EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged += EqCustomPresets_Changed;


        // 初始化多语言字符串
        _linguaSubs.Add(LanguageManager.Instance.Status_Disconnected.Subscribe(v => { if (v != null) _statusDisconnected = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Status_Connected.Subscribe(v => { if (v != null) _statusConnected = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Status_Identifying.Subscribe(v => { if (v != null) _statusIdentifying = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Status_Unidentified.Subscribe(v => { if (v != null) _statusUnidentified = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Feature_FindDevice.Subscribe(v => { if (v != null) _findDevice = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Feature_StopFindDevice.Subscribe(v => { if (v != null) _stopFindDevice = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Settings_CheckUpdate.Subscribe(v => { if (v != null) _checkUpdate = v; }));
        _linguaSubs.Add(LanguageManager.Instance.Settings_Checking.Subscribe(v => { if (v != null) _checking = v; }));

        // 主题：默认跟随系统
        var themeIndex = NextThemeIndex();
        ApplyTheme(themeIndex);
        CbTheme.SelectedIndex = themeIndex;

        BackgroundAnimationEnabled = false;

        // 透明度预设
        CbTransparencyPreset.SelectedIndex = 0;
        CbTransparencyPreset.SelectionChanged += (_, _) =>
        {
            if (_refreshingComboBoxes) return;
            ApplyTransparencyPreset(CbTransparencyPreset.SelectedIndex);
        };

        // 透明度：0 = 完全不透明，90 = 几乎透明
        var opacityVal = Math.Clamp(_uiSettings.GetInt("CardOpacity", 50), 0, 90);
        SlOpacity.Value = opacityVal;
        TbOpacity.Text = $"{opacityVal}%";
        SlOpacity.ValueChanged += (_, _) =>
        {
            var v = (int)SlOpacity.Value;
            TbOpacity.Text = $"{v}%";
            _uiSettings.SetInt("CardOpacity", v);
            if (!_applyingAppearancePreset) CbTransparencyPreset.SelectedIndex = 0;
            RefreshCardOpacity();
        };
        BtnResetOpacity.Click += (_, _) => SlOpacity.Value = 50;

        // 开机自启静默启动：只保留托盘，不显示主窗口，也不在任务栏生成最小化残影。
        if (App.IsMinimizedStartup())
            ShowInTaskbar = false;

        // 固定在屏幕右下角（已取消，使用默认居中）

        // Setup SukiUI toast host
        Hosts = [new SukiToastHost { Manager = ToastManager }];

        // 电池卡片：经 EarphoneImageProvider 加载（支持自定义图片覆盖）
        RefreshEarphoneImages();
        // 构建「自定义耳机图案」个性化设置行
        BuildEarphoneCustomUi();

        const string iconCharge = "M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z";
        // 每个 bolt 使用独立 Geometry 实例，避免共享同一 StreamGeometry 在 Avalonia 下偶发不渲染。
        LeftChargeBolt.Data = StreamGeometry.Parse(iconCharge);
        RightChargeBolt.Data = StreamGeometry.Parse(iconCharge);
        CaseChargeBolt.Data = StreamGeometry.Parse(iconCharge);
        _initializingSettings = true;
        try
        {
            InitializeLanguageSelection();
            CbTray.IsChecked = _uiSettings.GetBool("TrayEnabled", false);
            CbAuto.IsChecked = _uiSettings.GetBool("AutoStart", false);
            // 用 SetString/GetString 避免 SetBool(false) 删除条目导致默认值恢复
            var autoUpdate = _uiSettings.GetBool("AutoCheckUpdate", true) ? "true" : "false";
            CbAutoUpdate.IsChecked = autoUpdate != "false"; // 首次 null → true

            // 弹窗时长
            var durIdx = ReadToastDurationIndex();
            CbToastDuration.SelectedIndex = durIdx >= 0 && durIdx <= 5 ? durIdx : 2;
        }
        finally
        {
            _initializingSettings = false;
        }

        CbToastDuration.SelectionChanged += (_, _) =>
        {
            if (_refreshingComboBoxes || _initializingSettings) return;
            _uiSettings.SetInt("ToastDuration", ToastDurationSecondsFromIndex(CbToastDuration.SelectedIndex));
            _logManager?.Debug("UI", $"设置: Toast 时长索引 -> {CbToastDuration.SelectedIndex}");
        };

        // 背景设置
        RefreshBgThumbs();
        BgThumbDefault.PointerPressed += (_, _) => SelectBackground("default");
        BgThumbAdd.PointerPressed += (_, _) => _ = BtnBgAdd_Click();
        ApplySavedBackground();

        // 背景图片显示调节
        var bgBlur = _uiSettings.GetInt("BgBlur", 0);
        SlBgBlur.Value = bgBlur;
        TbBgBlur.Text = bgBlur.ToString();
        SlBgBlur.ValueChanged += (_, _) =>
        {
            var v = (int)SlBgBlur.Value;
            TbBgBlur.Text = v.ToString();
            _uiSettings.SetInt("BgBlur", v);
            var timer = EnsureBackgroundApplyDebounceTimer();
            timer.Stop();
            timer.Start();
        };
        BtnResetBgBlur.Click += (_, _) => SlBgBlur.Value = 0;

        // 高级自定义设置 (Beta)
        CbAdvancedRender.IsChecked = _uiSettings.GetBool("AdvancedRender", false);
        if (CbAdvancedRender.IsChecked == true) EnableAdvancedRender();
        CbAdvancedRender.IsCheckedChanged += (_, _) =>
        {
            var on = CbAdvancedRender.IsChecked == true;
            _uiSettings.SetBool("AdvancedRender", on);
            _logManager?.Debug("UI", $"设置: 高级渲染 -> {on}");
            if (on) EnableAdvancedRender();
            else DisableAdvancedRender();
        };

        // Acrylic 模糊开关
        CbAcrylicBlur.IsChecked = _uiSettings.GetBool("AcrylicBlur", false);
        if (CbAcrylicBlur.IsChecked == true)
            SelectBackground("default");
        UpdateBackgroundSettingsAvailability(CbAcrylicBlur.IsChecked == true);
        CbAcrylicBlur.IsCheckedChanged += (_, _) =>
            ToggleAcrylicBlur(CbAcrylicBlur.IsChecked == true);


        // 设备型号选择
        _brandTree = modelCatalog?.BrandTree
            ?? new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();

        CbBrand.ItemsSource = _brandList;
        CbSeries.ItemsSource = _seriesList;
        CbModel.ItemsSource = _modelList;

        _brandList.Add(LAutoDetect());
        foreach (var brand in _brandTree.Keys.OrderBy(b => b)) _brandList.Add(brand);

        _modelOverride = _uiSettings.GetString("ModelOverride");
        if (string.IsNullOrEmpty(_modelOverride))
        {
            CbBrand.SelectedItem = LAutoDetect();
        }
        else
        {
            var location = _modelCatalog?.FindLocation(_modelOverride);
            CbBrand.SelectedItem = location?.Brand ?? LAutoDetect();
            if (location is not null)
            {
                _seriesList.Clear();
                _seriesList.Add(LAllSeries());
                foreach (var s in _brandTree[location.Brand].Keys.OrderBy(s => s)) _seriesList.Add(s);
                CbSeries.SelectedItem = location.Series;
                _modelList.Clear();
                _modelList.Add(LAllModels());
                foreach (var m in _brandTree[location.Brand][location.Series]
                    .Select(model => model.DisplayName)
                    .OrderBy(model => model))
                    _modelList.Add(m);
                CbModel.SelectedItem = _modelOverride;
            }
        }


        var customName = _uiSettings.GetString("CustomName");
        TbCustomName.Text = customName ?? "";
        UpdateTitle();

        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            DisposeRuntimeUiResources();
            DisposeSukiWindow();
        };
        // 启动时明确显示主页，确保未连接设备时仍显示原项目的空降噪卡片。
        ShowPage("home");
        }
        catch (Exception ex)
        {
            _logManager?.Error("UI", "MainWindow 构造", ex);
            throw;
        }
    }

    // 迁移期间由 Next 应用生命周期注入控制器，具体设备逻辑仍由控制层负责。
    public MainWindow(
        FrontendState frontendState,
        ControlManager controlManager,
        ModelCatalog modelCatalog,
        OppoPodsManager.Assets.UserSettings.SettingsManager settings,
        ApplicationLog log,
        CommandDispatcher commandDispatcher,
        UpdateCoordinator? updateCoordinator = null,
        DesktopLinkService? desktopLinks = null,
        FeedbackExportService? feedbackExporter = null,
        Action? requestApplicationExit = null,
        Func<bool>? shouldKeepWindowAlive = null)
        : this(settings, modelCatalog, commandDispatcher, updateCoordinator, desktopLinks, feedbackExporter, requestApplicationExit, shouldKeepWindowAlive)
    {
        _frontendState = frontendState;
        _controlManager = controlManager;
        _controlManager.AvailableDevicesChanged += OnAvailableDevicesChanged;
        _logManager = log;
        _frontendState.Changed += OnNextStateChanged;
        PropertyChanged += OnWindowPropertyChanged;
        _logManager.Info("UI", "主窗口已接入 Next 控制层，禁用原项目连接循环。 ");
        ApplyNextSnapshot(_frontendState.Snapshot);
        _ = RefreshNextDevicesAsync();
    }

    private void OnAvailableDevicesChanged(object? sender, DeviceOptionsChangedEventArgs args)
    {
        if (_realClose)
            return;

        Dispatcher.UIThread.Post(() => ApplyNextDevices(args.Devices));
    }

    // 将控制层发布的不可变快照转换为主窗口当前视觉状态。
    private void OnNextStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        if (_realClose)
            return;

        Dispatcher.UIThread.Post(() => ApplyNextSnapshot(snapshot));
    }

    // 仅在主窗口可见时保持交互轮询租约，隐藏到托盘后释放轮询压力。
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_frontendState is null || e.Property != Visual.IsVisibleProperty)
            return;

        var visible = e.GetNewValue<bool>();
        if (visible)
            _interactiveSurface ??= _frontendState.AcquireInteractiveSurface();
        else
        {
            _interactiveSurface?.Dispose();
            _interactiveSurface = null;
        }
    }

    // 更新连接、电量、佩戴和设备信息；具体协议读取由控制层完成。
    private void ApplyNextSnapshot(BusinessSnapshot snapshot)
    {
        if (_realClose)
            return;

        // 使用原项目相同的连接状态文案，避免顶部直接显示不稳定的蓝牙设备名。
        UpdateNextConnectionStatus(snapshot);

        SetNextBattery(LeftLabel, LeftChargeBolt, LeftBatteryProgress, snapshot.LeftBattery);
        SetNextBattery(RightLabel, RightChargeBolt, RightBatteryProgress, snapshot.RightBattery);
        SetNextBattery(CaseLabel, CaseChargeBolt, CaseBatteryProgress, snapshot.CaseBattery);

        WearStatus.Text = string.Join("  ",
            new[]
            {
                snapshot.Wear.Left == EarWearState.Unknown ? null : DeviceText.WearState(snapshot.Wear.Left),
                snapshot.Wear.Right == EarWearState.Unknown ? null : DeviceText.WearState(snapshot.Wear.Right)
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
        WearStatus.IsVisible = !string.IsNullOrWhiteSpace(WearStatus.Text);

        DiDeviceName.Text = DeviceText.DeviceName(snapshot.Identity?.DisplayName, snapshot.DeviceName);
        DiFirmware.Text = snapshot.Identity?.FirmwareVersion ?? "-";
        DiCodec.Text = snapshot.Identity?.Codec ?? "-";
        UpdateTitle();
        var nextPresentation = _controlManager?.ActiveManager?.Presentation;
        var nextModelName = nextPresentation?.IsKnownModel == true
            ? nextPresentation.ModelName
            : snapshot.Identity?.ModelName ?? snapshot.Identity?.DisplayName ?? snapshot.DeviceName;
        ModelNote.Text = snapshot.IsConnected && !string.IsNullOrWhiteSpace(nextModelName)
            ? string.Format(
                LanguageManager.Instance.GetString(_modelOverride is null
                    ? LanguageManager.Instance.Settings_ModelAutoDetected
                    : LanguageManager.Instance.Settings_ModelManualSet),
                nextModelName)
            : string.Empty;
        ApplyNextFeatureState(snapshot);
        ApplyNextSpatialAudioSnapshot(snapshot);
        ApplyNextEqualizerSnapshot(snapshot);
        ApplyNextNoiseSnapshot(snapshot);
        SyncNextMultiDeviceList(snapshot);
        BtnReconnect.IsVisible = !snapshot.IsConnected;
        _logManager?.Debug("UI", $"快照已应用：revision={snapshot.Revision}，connected={snapshot.IsConnected}，device={snapshot.DeviceName ?? ""}。");
    }

    // 将 Next 快照转换为原项目风格的连接状态和型号识别文案。
    private void UpdateNextConnectionStatus(BusinessSnapshot snapshot)
    {
        StatusDot.Fill = snapshot.IsConnected ? BrushGreen : BrushRed;
        StatusText.Foreground = snapshot.IsConnected ? BrushLightGreen : BrushLightRed;

        if (!snapshot.IsConnected)
        {
            _wasConnected = false;
            _connectionStatusStartedAt = DateTime.MinValue;
            StatusText.Text = TranslationCatalog.Get("Status_Disconnected");
            return;
        }

        if (!_wasConnected)
        {
            _wasConnected = true;
            _connectionStatusStartedAt = DateTime.Now;
        }

        var manager = _controlManager?.ActiveManager;
        var modelName = manager?.Presentation.IsKnownModel == true
            ? manager.Presentation.ModelName
            : snapshot.Identity?.ModelName;
        if (!string.IsNullOrWhiteSpace(modelName)
            && !string.Equals(modelName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var connectedText = string.IsNullOrWhiteSpace(_statusConnected)
                ? TranslationCatalog.Get("Status_Connected")
                : _statusConnected;
            StatusText.Text = string.Format(connectedText, modelName);
            return;
        }

        var identifyingText = string.IsNullOrWhiteSpace(_statusIdentifying)
            ? TranslationCatalog.Get("Status_Identifying")
            : _statusIdentifying;
        var unidentifiedText = string.IsNullOrWhiteSpace(_statusUnidentified)
            ? TranslationCatalog.Get("Status_Unidentified")
            : _statusUnidentified;
        StatusText.Text = DateTime.Now - _connectionStatusStartedAt < TimeSpan.FromSeconds(2)
            ? identifyingText
            : unidentifiedText;
    }

    // 将控制层功能状态静默回显到原项目保留的开关控件。
    private void ApplyNextFeatureState(BusinessSnapshot snapshot)
    {
        var manager = _controlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        var hasFeatures = snapshot.IsConnected && presentation is not null;
        FeatureContentPanel.IsVisible = hasFeatures;
        FeaturePlaceholderText.IsVisible = !hasFeatures;
        if (!hasFeatures)
        {
            BtnFindDevice.IsVisible = false;
            SetFeatureControlsEnabled(false);
            _findDeviceActive = false;
            BtnFindDevice.Content = _findDevice;
            return;
        }

        var visibleControls = presentation!.VisibleControls;
        var controlStates = presentation.ControlStates;
        var controlEnabledStates = presentation.ControlEnabledStates;
        SetFeatureControlsEnabled(controlEnabledStates);
        CbDualDevice.IsVisible = visibleControls.Contains("dual-device");
        CbBassEngine.IsVisible = visibleControls.Contains("bass-engine");
        CbVocalEnhance.IsVisible = visibleControls.Contains("voice-enhancement");
        CbHearingEnhance.IsVisible = visibleControls.Contains("hearing-enhancement");
        CbLongPower.IsVisible = visibleControls.Contains("long-battery");
        CbWearDetection.IsVisible = visibleControls.Contains("wear-detection");
        CbSpineHealth.IsVisible = visibleControls.Contains("spine-health");
        CbSpatial.IsVisible = visibleControls.Contains("spatial-sound");
        CbGameSound.IsVisible = visibleControls.Contains("game-sound");
        CbGame.IsVisible = visibleControls.Contains("game-mode");
        BtnFindDevice.IsVisible = visibleControls.Contains("find-device");
        var hasVisibleFeature = CbDualDevice.IsVisible
            || CbBassEngine.IsVisible
            || CbVocalEnhance.IsVisible
            || CbHearingEnhance.IsVisible
            || CbLongPower.IsVisible
            || CbWearDetection.IsVisible
            || CbSpineHealth.IsVisible
            || CbSpatial.IsVisible
            || CbGameSound.IsVisible
            || CbGame.IsVisible
            || BtnFindDevice.IsVisible;
        FeatureContentPanel.IsVisible = hasVisibleFeature;
        FeaturePlaceholderText.IsVisible = !hasVisibleFeature;
        // 查找耳机不再按佩戴状态禁用：无论是否佩戴都允许点击，点击后弹安全警告（防止戴耳内响铃致听力损伤）。
        BtnFindDevice.IsEnabled = BtnFindDevice.IsVisible && snapshot.IsConnected;

        SetNextCheck(CbDualDevice, controlStates, "dual-device", CbDualDevice_Changed);
        SetNextCheck(CbBassEngine, controlStates, "bass-engine", CbBassEngine_Changed);
        SetNextCheck(CbVocalEnhance, controlStates, "voice-enhancement", CbVocalEnhance_Changed);
        SetNextCheck(CbHearingEnhance, controlStates, "hearing-enhancement", CbHearingEnhance_Changed);
        SetNextCheck(CbLongPower, controlStates, "long-battery", CbLongPower_Changed);
        SetNextCheck(CbWearDetection, controlStates, "wear-detection", CbWearDetection_Changed);
        SetNextCheck(CbSpineHealth, controlStates, "spine-health", CbSpineHealth_Changed);
        SetNextCheck(CbSpatial, controlStates, "spatial-sound", CbSpatial_Changed);
        // 游戏音效状态来自 0x812B 的 SoundType，不能使用 0x810D 的功能探针值回显。
        if (!_gameSoundCommandPending && snapshot.Game.SoundType is { } gameSoundType)
            SetGameSoundCheckedSilent(gameSoundType != 0);
        if (controlStates.TryGetValue("game-mode", out var gameMode))
            SetGameCheckedSilent(gameMode);

        SetGameSoundEnabledSilent(GetControlEnabled(controlEnabledStates, "game-sound"));
        SetSpatialEnabledSilent(GetControlEnabled(controlEnabledStates, "spatial-sound"));
        SetEqControlsEnabled(GetControlEnabled(controlEnabledStates, "equalizer"));
    }

    // 读取控制层计算出的可操作状态，缺省保持控件可用。
    private static bool GetControlEnabled(IReadOnlyDictionary<string, bool> states, string key)
        => !states.TryGetValue(key, out var enabled) || enabled;

    // 将控制层返回的功能可用状态映射到原版控件。
    private void SetFeatureControlsEnabled(bool enabled)
    {
        SetControlEnabledSilent(CbDualDevice, enabled);
        SetControlEnabledSilent(CbBassEngine, enabled);
        SetControlEnabledSilent(CbVocalEnhance, enabled);
        SetControlEnabledSilent(CbHearingEnhance, enabled);
        SetControlEnabledSilent(CbLongPower, enabled);
        SetControlEnabledSilent(CbWearDetection, enabled);
        SetControlEnabledSilent(CbSpineHealth, enabled);
        SetControlEnabledSilent(CbSpatial, enabled);
        SetControlEnabledSilent(CbGameSound, enabled);
        SetControlEnabledSilent(CbGame, enabled);
        SetControlEnabledSilent(BtnFindDevice, enabled);
        SetEqControlsEnabled(enabled);
    }

    // 按控制层能力字典逐项设置功能控件的可操作状态。
    private void SetFeatureControlsEnabled(IReadOnlyDictionary<string, bool> states)
    {
        SetControlEnabledSilent(CbDualDevice, GetControlEnabled(states, "dual-device"));
        SetControlEnabledSilent(CbBassEngine, GetControlEnabled(states, "bass-engine"));
        SetControlEnabledSilent(CbVocalEnhance, GetControlEnabled(states, "voice-enhancement"));
        SetControlEnabledSilent(CbHearingEnhance, GetControlEnabled(states, "hearing-enhancement"));
        SetControlEnabledSilent(CbLongPower, GetControlEnabled(states, "long-battery"));
        SetControlEnabledSilent(CbWearDetection, GetControlEnabled(states, "wear-detection"));
        SetControlEnabledSilent(CbSpineHealth, GetControlEnabled(states, "spine-health"));
        SetControlEnabledSilent(CbGame, GetControlEnabled(states, "game-mode"));
        SetControlEnabledSilent(BtnFindDevice, GetControlEnabled(states, "find-device"));
        SetGameSoundEnabledSilent(GetControlEnabled(states, "game-sound"));
        SetSpatialEnabledSilent(GetControlEnabled(states, "spatial-sound"));
        SetEqControlsEnabled(GetControlEnabled(states, "equalizer"));
    }

    // 根据 Next 状态回显空间音频，并在首次识别设备后创建原版风格的单选项。
    private void ApplyNextSpatialAudioSnapshot(BusinessSnapshot snapshot)
    {
        var manager = _controlManager?.ActiveManager;
        if (manager is null || !snapshot.IsConnected)
        {
            SpatialAudioPanel.IsVisible = false;
            SpatialAudioModes.Children.Clear();
            return;
        }

        var enabled = manager.Presentation.SupportsSpatialAudio;
        if (!enabled)
        {
            SpatialAudioPanel.IsVisible = false;
            SpatialAudioModes.Children.Clear();
            return;
        }

        SpatialAudioPanel.IsVisible = true;

        if (SpatialAudioModes.Children.Count == 0)
        {
            foreach (var (mode, key) in new[]
            {
                ("Off", "SpatialAudio_ModeOff"),
                ("Fixed", "SpatialAudio_ModeFixed"),
                ("HeadTracking", "SpatialAudio_ModeHeadTrack")
            })
            {
                var button = new RadioButton
                {
                    Content = TranslationCatalog.Get(key),
                    Tag = mode,
                    GroupName = "SpatialAudioMode",
                    Margin = new Thickness(8, 2)
                };
                button.IsCheckedChanged += SpatialAudio_Changed;
                SpatialAudioModes.Children.Add(button);
            }
        }

        var selected = snapshot.SpatialAudio.Mode switch
        {
            SpatialAudioMode.Fixed => "Fixed",
            SpatialAudioMode.HeadTracking => "HeadTracking",
            _ => "Off"
        };
        foreach (var control in SpatialAudioModes.Children)
        {
            if (control is not RadioButton button || button.Tag is not string tag)
                continue;
            button.IsCheckedChanged -= SpatialAudio_Changed;
            button.IsChecked = tag == selected;
            button.IsCheckedChanged += SpatialAudio_Changed;
        }
    }

    // 只更新开关的选中态，避免设备回读再次触发写命令。
    private static void SetNextCheck(
        CheckBox checkBox,
        IReadOnlyDictionary<string, bool> states,
        string featureName,
        EventHandler<RoutedEventArgs> handler)
    {
        if (states.TryGetValue(featureName, out var enabled))
        {
            checkBox.IsCheckedChanged -= handler;
            checkBox.IsChecked = enabled;
            checkBox.IsCheckedChanged += handler;
        }
    }

    // 显示 Next 快照中的单个电量、进度颜色和充电状态。
    private static void SetNextBattery(TextBlock label, AvaloniaControl bolt, ProgressBar progress, BatteryLevel? battery)
    {
        if (battery is not { } value)
        {
            label.Text = "-%";
            bolt.IsVisible = false;
            progress.Value = 0;
            progress.IsVisible = false;
            return;
        }

        label.Text = $"{value.Percent}%";
        bolt.IsVisible = value.IsCharging;
        progress.Value = value.Percent;
        progress.Foreground = value.Percent <= 20
            ? BrushBatteryLow
            : value.Percent <= 60 ? BrushBatteryMid : BrushBatteryHigh;
        progress.IsVisible = true;
    }

    // 将 Next 能力和设备通知转换为原版 EQ 列表显示，避免把协议编号直接显示给用户。
    private void ApplyNextEqualizerSnapshot(BusinessSnapshot snapshot)
    {
        // 新建或保存期间保留编辑卡片，避免内置 EQ 的中间快照覆盖用户正在编辑的内容。
        if (_nextEqEditing)
            return;

        var manager = _controlManager?.ActiveManager;
        _eqSuppressListEvent = true;
        _refreshingComboBoxes = true;
        try
        {
            LbEqBuiltinPresets.Items.Clear();
            LbEqCustomPresets.Items.Clear();
            CbEq.Items.Clear();

            if (!snapshot.IsConnected || manager is null)
            {
                BtnEqNew.IsVisible = false;
                EqCustomPresetPanel.IsVisible = false;
                EqSliderCard.IsVisible = false;
                BtnEqSave.IsEnabled = false;
                _eqCurrentPreset = string.Empty;
                _eqCurrentId = 0;
                _synchronizingNextEq = true;
                try
                {
                    ConfigureNextEqBands(
                        [],
                        BrandPresentation.DefaultCustomEqMinimumGain,
                        BrandPresentation.DefaultCustomEqMaximumGain);
                    SetAllEqSliders(0);
                }
                finally
                {
                    _synchronizingNextEq = false;
                }
                return;
            }

            // 新建入口仅在型号能力明确支持自定义 EQ 时显示。
            var presentation = manager.Presentation;
            var supportsCustomEq = presentation.SupportsCustomEqualizer;
            BtnEqNew.IsVisible = supportsCustomEq;
            EqCustomPresetPanel.IsVisible = supportsCustomEq;
            ConfigureNextEqBands(
                presentation.CustomEqFrequencies,
                presentation.CustomEqMinimumGain,
                presentation.CustomEqMaximumGain);

            foreach (var presetName in presentation.EqualizerPresets)
            {
                var displayName = DeviceProfileLoader.LocalizedEqName(presetName);
                LbEqBuiltinPresets.Items.Add(new EqPresetItem
                {
                    Name = presetName,
                    DisplayName = displayName,
                    IsCustom = false
                });
                CbEq.Items.Add(presetName);
            }

            foreach (var entry in supportsCustomEq ? snapshot.EqualizerEntries : [])
            {
                if (string.IsNullOrWhiteSpace(entry.Name)
                    || presentation.EqualizerPresets.Contains(entry.Name))
                    continue;

                LbEqCustomPresets.Items.Add(new EqPresetItem
                {
                    Name = entry.Name,
                    // 设备端和用户自定义名称是协议数据，显示时保留原文。
                    DisplayName = string.IsNullOrWhiteSpace(entry.Name)
                        ? $"EQ {entry.Id}"
                        : entry.Name,
                    IsCustom = false,
                    EqId = entry.Id
                });
                if (!CbEq.Items.Contains(entry.Name))
                    CbEq.Items.Add(entry.Name);
            }

            var selectedName = snapshot.Equalizer.PresetName
                ?? snapshot.EqualizerEntries.FirstOrDefault(entry => entry.IsSelected)?.Name;
            if (string.IsNullOrWhiteSpace(selectedName))
                return;

            var selectedEntry = supportsCustomEq
                ? snapshot.EqualizerEntries.FirstOrDefault(entry => entry.Name == selectedName)
                : null;
            if (selectedEntry is not null)
            {
                LbEqCustomPresets.SelectedItem = LbEqCustomPresets.Items
                    .OfType<EqPresetItem>()
                    .FirstOrDefault(item => item.Name == selectedName);
                EqSliderCard.IsVisible = true;
                BtnEqSave.IsEnabled = true;
                _synchronizingNextEq = true;
                try
                {
                    ApplyNextEqEntry(selectedEntry);
                }
                finally
                {
                    _synchronizingNextEq = false;
                }
            }
            else
            {
                LbEqBuiltinPresets.SelectedItem = LbEqBuiltinPresets.Items
                    .OfType<EqPresetItem>()
                    .FirstOrDefault(item => item.Name == selectedName);
                EqSliderCard.IsVisible = false;
                BtnEqSave.IsEnabled = false;
                ConfigureNextEqBands(
                    [],
                    presentation.CustomEqMinimumGain,
                    presentation.CustomEqMaximumGain);
            }

            CbEq.SelectedItem = selectedName;
        }
        finally
        {
            _refreshingComboBoxes = false;
            _eqSuppressListEvent = false;
        }
    }

    private async void RefreshDevices_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "用户操作: 刷新多耳机列表");
        if (_controlManager is null)
            return;

        var devices = await _controlManager.RefreshAvailableDevicesAsync(CancellationToken.None);
        ApplyNextDevices(devices);
    }

    // 将控制层返回的设备选择项显示到原项目保留的下拉控件。
    private void ApplyNextDevices(IReadOnlyList<DeviceConnectionOption> devices)
    {
        var selectedId = (CbDevice.SelectedItem as DeviceConnectionOption)?.Id
            ?? _controlManager?.ActiveDeviceId;
        _nextDevices.Clear();
        _nextDevices.AddRange(devices);
        _suppressEarbudSelection = true;
        CbDevice.Items.Clear();
        foreach (var device in _nextDevices)
            CbDevice.Items.Add(device);
        CbDevice.IsVisible = _nextDevices.Count > 0;
        BtnRefreshDevices.IsVisible = _nextDevices.Count > 0;

        var selectedIndex = selectedId is null
            ? -1
            : _nextDevices.FindIndex(device => string.Equals(device.Id, selectedId, StringComparison.Ordinal));
        if (selectedIndex < 0 && _nextDevices.Count > 0 && _controlManager?.ActiveManager is null)
            selectedIndex = 0;
        CbDevice.SelectedIndex = selectedIndex;
        _suppressEarbudSelection = false;
        _logManager?.Debug("UI", $"设备选择列表已更新：count={_nextDevices.Count}，selected={selectedId ?? ""}。");
    }

    private void CbDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEarbudSelection || _controlManager is null
            || CbDevice.SelectedIndex < 0 || CbDevice.SelectedIndex >= _nextDevices.Count)
            return;

        var selected = _nextDevices[CbDevice.SelectedIndex];
        _logManager?.Info("UI", $"用户选择设备：id={selected.Id}，name={selected.DisplayName}。");
        _ = ConnectNextDeviceAsync(selected.Id);
    }

    private DispatcherTimer EnsureEqDebounceTimer()
    {
        if (_eqDebounceTimer != null)
            return _eqDebounceTimer;

        _eqDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _eqDebounceTimer?.Stop();
            SendCurrentCustomEq();
        });
        return _eqDebounceTimer;
    }

    private DispatcherTimer EnsureBackgroundApplyDebounceTimer()
    {
        if (_bgApplyDebounceTimer != null)
            return _bgApplyDebounceTimer;

        _bgApplyDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background, (_, _) =>
        {
            _bgApplyDebounceTimer?.Stop();
            ApplySavedBackground();
        });
        return _bgApplyDebounceTimer;
    }

    private void CbTray_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        var on = CbTray.IsChecked == true;
        _uiSettings.SetBool("TrayEnabled", on);
        _logManager?.Debug("UI", $"设置: 关闭到托盘 -> {on}");
    }

    private void CbAuto_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        var on = CbAuto.IsChecked == true;
        _uiSettings.SetBool("AutoStart", on);
        _logManager?.Debug("UI", $"设置: 开机自启 -> {on}");
    }
    private void CbAutoUpdate_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        var on = CbAutoUpdate.IsChecked == true;
        _uiSettings.SetBool("AutoCheckUpdate", on);
        _logManager?.Debug("UI", $"设置: 自动检查更新 -> {on}");
    }

    /// <summary>按控制层提供的降噪展示模型生成主/子模式圆形图标按钮。</summary>
    private void BuildAncUi(string modelKey, IReadOnlyList<NoiseOptionModel> options)
    {
        var buildKey = modelKey + "|" + string.Join("|", options.Select(opt =>
            opt.Children.Count > 0
                ? $"{opt.Key}:{DeviceProfileLoader.AncLabel(opt.Key)}>" + string.Join(",", opt.Children.Select(c => $"{c.Key}:{DeviceProfileLoader.AncLabel(c.Key)}"))
                : $"{opt.Key}:{DeviceProfileLoader.AncLabel(opt.Key)}"));
        if (_ancBuiltForModel == buildKey) return;
        _ancBuiltForModel = buildKey;
        _ancOptions = options.ToList();

        _ancMainButtons.Clear();
        _ancChildToMain.Clear();
        AncMainRow.Children.Clear();
        AncMainRow.ColumnDefinitions.Clear();
        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        _ancSubButtons.Clear();
        _ancSubSignature = "";
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
    private void PopulateAncSub(NoiseOptionModel container)
    {
        var signature = container.Key + ":" + string.Join("|", container.Children.Select(c => $"{c.Key};{DeviceProfileLoader.AncLabel(c.Key)}"));
        if (_ancSubSignature == signature && _ancSubButtons.Count == container.Children.Count)
            return;

        foreach (var (_, (btn, _)) in _ancSubButtons)
            btn.Click -= AncSub_Click;

        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        _ancSubButtons.Clear();
        _ancSubSignature = signature;

        int col = 0;
        for (int i = 0; i < container.Children.Count; i++)
        {
            var child = container.Children[i];
            if (i > 0) AddSeparator(AncSubRow, ref col);
            var corner = FirstLast(i, container.Children.Count, 5);
            var (btn, bg) = MakeTextButton(DeviceProfileLoader.AncLabel(child.Key), child, 72, 28, 13, corner, AncSub_Click);
            AddToRow(AncSubRow, bg, ref col);
            _ancSubButtons[child.Key] = (btn, bg);
        }
    }

    /// <summary>切换语言后，用实时本地化标签刷新已生成的 ANC 主/子按钮文字。</summary>
    private void RefreshAncLabels()
    {
        foreach (var (key, (_, _, label)) in _ancMainButtons)
        {
            var t = DeviceProfileLoader.AncLabel(key);
            label.Text = t;
            label.FontSize = t.Length > 10 ? 9 : 11;
        }
        foreach (var (key, (btn, _)) in _ancSubButtons)
            btn.Content = DeviceProfileLoader.AncLabel(key);
    }

    /// <summary>创建图标+文字按钮：Ellipse 圆形背景+描边 + 矢量图标 + 文字。</summary>
    private (AvaloniaControl panel, Ellipse bg, Ellipse stroke, Path icon, TextBlock label) MakeAncIconButton(
        NoiseOptionModel opt, int circleSize, int iconSize, int fontSize,
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
            Data = Avalonia.Media.StreamGeometry.Parse(AncIcons.GetAncIcon(opt.Key)),
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

        var labelText = DeviceProfileLoader.AncLabel(opt.Key);
        var label = new TextBlock
        {
            Text = labelText,
            FontSize = labelText.Length > 10 ? Math.Max(9, fontSize - 2) : fontSize,
            Foreground = BrushGray,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(label);

        // bg 用于填充，icon 用于图标+描边
        return (panel, bg, bg, icon, label);
    }

    private void AddToRow(Grid row, AvaloniaControl c, ref int col)
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

    private (Button, Border) MakeTextButton(string label, NoiseOptionModel opt, int w, int h, int fontSize, CornerRadius corner, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Button
        {
            Content = label, Tag = opt, MinWidth = w, Height = h,
            BorderThickness = new Thickness(0), Padding = new Thickness(8, 0),
            Background = BrushTransparent, Focusable = false,
            Foreground = BrushGray, FontSize = fontSize,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        btn.Click += onClick;
        var bg = new Border { CornerRadius = corner, Padding = new Thickness(0), Background = BrushTransparent, Child = btn };
        return (btn, bg);
    }

    private void HighlightAnc()
    {
        var circleGray = GetCircleGray();
        var accent = _isLightTheme ? (IBrush)_brushAccentLight : (IBrush)BrushAccent;
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

    private void SwitchAncMain(NoiseOptionModel opt)
    {
        _logManager?.Debug("UI", $"用户操作: ANC 主模式 -> {opt.Key}");
        if (_controlManager?.ActiveManager is null) return;
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
            _ = _commandDispatcher?.RunAsync("ANC 子模式", manager => manager.SetNoiseCancellationByKeyAsync(target, CancellationToken.None));
        }
        else
        {
            // 叶子型：直接发送，收起子模式
            AncSubRow.IsVisible = false;
            _ancLevel = "";
            _ = _commandDispatcher?.RunAsync("ANC 主模式", manager => manager.SetNoiseCancellationByKeyAsync(opt.Key, CancellationToken.None));
        }
        HighlightAnc();
    }

    private void SwitchAncSub(NoiseOptionModel opt)
    {
        _logManager?.Debug("UI", $"用户操作: ANC 子级别 -> {opt.Key}");
        if (_controlManager?.ActiveManager is null) return;
        _ancLevel = opt.Key;
        _ancLastSub[_ancMain] = opt.Key;
        _ = _commandDispatcher?.RunAsync("ANC 子模式", manager => manager.SetNoiseCancellationByKeyAsync(opt.Key, CancellationToken.None));
        HighlightAnc();
    }

    private void AncMain_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is NoiseOptionModel opt) SwitchAncMain(opt);
    }

    private void AncSub_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is NoiseOptionModel opt) SwitchAncSub(opt);
    }

    // 本地化辅助：型号选择 ComboBox 哨兵项（显示与比较共用，必须保持一致）。
    private static string LAutoDetect() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AutoDetect);
    private static string LAllSeries() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AllSeries);
    private static string LAllModels() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AllModels);

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
        if (s is RadioButton button && button.IsChecked == true && button.Tag is string mode)
            _ = _commandDispatcher?.RunAsync("空间音频", manager => manager.SetSpatialAudioByKeyAsync(mode, CancellationToken.None));
    }

    private void CbSpatial_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbSpatial.IsChecked is { } nextOn)
            _ = _commandDispatcher?.RunAsync("空间声场", manager => manager.SetSpatialSoundAsync(nextOn, CancellationToken.None));
    }

    private void CbGame_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbGame.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("游戏模式", manager => manager.SetGameModeAsync(enabled, CancellationToken.None));
    }

    private void CbGameSound_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbGameSound.IsChecked is { } enabled)
            _ = RunGameSoundCommandAsync(enabled);
    }

    private void SetEqControlsEnabled(bool enabled)
    {
        // 均衡器滑块 + dB 标签
        EqSlider62.IsEnabled = enabled;
        EqSlider250.IsEnabled = enabled;
        EqSlider1k.IsEnabled = enabled;
        EqSlider4k.IsEnabled = enabled;
        EqSlider8k.IsEnabled = enabled;
        EqSlider16k.IsEnabled = enabled;
        EqDb62.Opacity = enabled ? 0.5 : 0.2;
        EqDb250.Opacity = enabled ? 0.5 : 0.2;
        EqDb1k.Opacity = enabled ? 0.5 : 0.2;
        EqDb4k.Opacity = enabled ? 0.5 : 0.2;
        EqDb8k.Opacity = enabled ? 0.5 : 0.2;
        EqDb16k.Opacity = enabled ? 0.5 : 0.2;
        // 预设列表 + 新建按钮
        LbEqBuiltinPresets.IsEnabled = enabled;
        LbEqCustomPresets.IsEnabled = enabled;
        BtnEqNew.IsEnabled = enabled;
    }

    /// <summary>不触发事件地设置游戏音效勾选态（避免互斥联动递归下发命令）。</summary>
    private void SetGameSoundCheckedSilent(bool value)
    {
        // 快照频繁更新时保持同值不重写，避免 CheckBox 状态动画反复闪烁。
        if (CbGameSound.IsChecked == value)
            return;

        CbGameSound.IsCheckedChanged -= CbGameSound_Changed;
        CbGameSound.IsChecked = value;
        CbGameSound.IsCheckedChanged += CbGameSound_Changed;
    }

    // 快照同步时仅在互斥状态变化后更新控件，避免重复触发 Suki 控件重绘。
    private static void SetControlEnabledSilent(Avalonia.Controls.Control control, bool enabled)
    {
        if (control.IsEnabled != enabled)
            control.IsEnabled = enabled;
    }

    private void SetGameSoundEnabledSilent(bool enabled)
        => SetControlEnabledSilent(CbGameSound, enabled);

    private void SetSpatialEnabledSilent(bool enabled)
        => SetControlEnabledSilent(CbSpatial, enabled);

    /// <summary>不触发事件地设置游戏模式勾选态（用于初始化/轮询回读，非用户操作）。</summary>
    private void SetGameCheckedSilent(bool value)
    {
        CbGame.IsCheckedChanged -= CbGame_Changed;
        CbGame.IsChecked = value;
        CbGame.IsCheckedChanged += CbGame_Changed;
    }

    private void CbDualDevice_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbDualDevice.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("双设备", manager => manager.SetDualDeviceAsync(enabled, CancellationToken.None));
    }

    private void CbBassEngine_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbBassEngine.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("低音引擎", manager => manager.SetBassEngineAsync(enabled, CancellationToken.None));
    }

    private void CbVocalEnhance_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbVocalEnhance.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("人声增强", manager => manager.SetVoiceEnhancementAsync(enabled, CancellationToken.None));
    }

    private void CbHearingEnhance_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbHearingEnhance.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("听力增强", manager => manager.SetHearingEnhancementAsync(enabled, CancellationToken.None));
    }

    private void CbLongPower_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbLongPower.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("长续航", manager => manager.SetLongBatteryAsync(enabled, CancellationToken.None));
    }

    private void CbWearDetection_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbWearDetection.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("佩戴检测", manager => manager.SetWearDetectionAsync(enabled, CancellationToken.None));
    }

    private void CbSpineHealth_Changed(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CbSpineHealth.IsChecked is { } enabled)
            _ = _commandDispatcher?.RunAsync("脊柱健康", manager => manager.SetSpineHealthAsync(enabled, CancellationToken.None));
    }

    private async void BtnFindDevice_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_controlManager?.ActiveManager is null)
            return;

        // 仅在“启动查找”时弹安全警告（查找耳机会让耳机发出较大响铃，戴在耳内可能致听力损伤）。
        // “停止查找”无风险，不弹窗。
        if (!_findDeviceActive)
        {
            var confirmed = await ShowFindWarningDialog();
            if (!confirmed)
                return;
        }

        _findDeviceActive = !_findDeviceActive;
        BtnFindDevice.Content = _findDeviceActive ? _stopFindDevice : _findDevice;
        _ = _commandDispatcher?.RunAsync("查找耳机", manager => manager.SetFindDeviceAsync(_findDeviceActive, CancellationToken.None));
    }

    private void CbEq_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes || CbEq.SelectedItem is not string name)
            return;

        _ = _commandDispatcher?.RunAsync("EQ 预设", manager => manager.SetEqualizerByNameAsync(name, CancellationToken.None));
    }

    private void CbBrand_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || brand == LAutoDetect())
        {
            _seriesList.Clear();
            _modelList.Clear();
            return;
        }
        if (!_brandTree.TryGetValue(brand, out var series)) return;

        _seriesList.Clear();
        _seriesList.Add(LAllSeries());
        foreach (var sn in series.Keys.OrderBy(x => x)) _seriesList.Add(sn);
        CbSeries.SelectedItem = LAllSeries();
    }

    private void CbSeries_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || !_brandTree.TryGetValue(brand, out var series)) return;
        var sn = CbSeries.SelectedItem as string ?? LAllSeries();

        _modelList.Clear();
        _modelList.Add(LAllModels());
        var models = sn == LAllSeries()
            ? series.SelectMany(kv => kv.Value).Select(model => model.DisplayName).Distinct().OrderBy(x => x).ToList()
            : series.TryGetValue(sn, out var list)
                ? list.Select(model => model.DisplayName).OrderBy(x => x).ToList()
                : new List<string>();
        foreach (var m in models) _modelList.Add(m);
        CbModel.SelectedItem = LAllModels();
    }

    private void CbModel_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbModel.SelectedItem is string model && model != LAllModels())
        {
            _logManager?.Debug("UI", $"用户操作: 手动指定机型 -> {model}");
            _modelOverride = model;
            WriteUiString("ModelOverride", model);
            SyncCaps();
        }
        else
        {
            _modelOverride = null;
            WriteUiString("ModelOverride", null);
            SyncCaps();
        }
    }

    private void SyncCaps()
    {
        _controlManager?.SetManualModel(_modelOverride);
    }

    private void CbTheme_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes) return;
        var idx = CbTheme.SelectedIndex;
        _logManager?.Debug("UI", $"用户操作: 切换主题 -> {idx}");
        ApplyTheme(idx);
        _uiSettings.SetString("Theme", NextThemeName(idx));
    }

    private void InitializeLanguageSelection()
    {
        _languageList.Clear();
        foreach (var option in LanguageManager.GetAvailableLanguages())
            _languageList.Add(option);

        CbLanguage.ItemsSource = _languageList;
        var configured = _uiSettings.GetString("Language");
        var selected = _languageList.FirstOrDefault(option =>
            string.Equals(option.CultureCode,
                LanguageManager.NormalizeSelectionCulture(configured),
                StringComparison.OrdinalIgnoreCase));
        CbLanguage.SelectedItem = selected ?? _languageList[0];
    }

    private void CbLanguage_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes || _initializingSettings || CbLanguage.SelectedItem is not LanguageOption option)
            return;

        _uiSettings.SetString("Language", LanguageManager.ToStoredLanguage(option));
        LanguageManager.ApplyConfiguredCulture(option.IsAutomatic ? null : option.CultureCode);
        // 语言列表里的"自动"项在初始化时按启动语言本地化，切换语言后需重新取当前语言的显示文本。
        // 必须延迟到本次 SelectionChanged 的选择更新结束后再改源集合，否则 Avalonia 抛
        // "Source collection was modified during selection update"。
        var autoOption = LanguageManager.GetAvailableLanguages()[0];
        Dispatcher.UIThread.Post(() =>
        {
            _refreshingComboBoxes = true;
            try
            {
                // 先记录当前选中索引。替换首项（自动）后，若该对象正是当前选中项且其
                // 本地化文本随语言变化（如从 English 切到“自动”会解析成中文“自动”），
                // record 值不再相等，Avalonia 会判定选中项已离开列表而清空选择——下拉框
                // 会显示空白。按原索引重新选中即可恢复正确的显示文本。
                var selIdx = CbLanguage.SelectedIndex;
                _languageList[0] = autoOption;
                if (selIdx >= 0 && selIdx < _languageList.Count)
                    CbLanguage.SelectedIndex = selIdx;
            }
            finally
            {
                _refreshingComboBoxes = false;
            }
        });
        RefreshLocalizedComboBoxes();
        RefreshAncLabels();
        RefreshSpatialAudioLabels();
        // 自定义耳机图案行的文案需随语言重建
        BuildEarphoneCustomUi();
        // "恢复已隐藏设备"按钮文字由代码动态设置（带计数），切语言后需重新本地化
        RefreshRestoreHiddenDevicesButton();
        // 顶栏连接状态与佩戴状态文字在状态事件里赋值，切语言后需重新刷新一次，
        // 否则会停留在旧语言直到下次状态变化。
        if (_frontendState is not null)
            ApplyNextSnapshot(_frontendState.Snapshot);
        // 音效页系统预设名按当前语言本地化（DisplayName），切语言后需重建列表以刷新显示。
        if (_frontendState is not null)
            ApplyNextEqualizerSnapshot(_frontendState.Snapshot);
        // 托盘菜单/提示含冻结中文（ANC 标签、功能项、显示主页面/退出），强制重建
        // 优先设备下拉的中文选项（自动选择/未知设备/占位符）在策略同步时重建；
        // 重置签名确保语言切换后使用本地化文本。
        _priorityOptionsSignature = "";
        // Force rebuild multi-device list with new language strings
        SyncNextMultiDeviceList(_frontendState?.Snapshot);
    }

    /// <summary>
    /// Force ComboBox presenters to re-evaluate the selected item's display text after
    /// a language change. The {Translate} bindings update ComboBoxItem.Content, but
    /// Avalonia's ComboBox caches the display in SelectionBoxItem at selection time and
    /// does not re-read Content when it changes. Re-assigning SelectedIndex forces it to
    /// re-evaluate from the bound Content.
    /// </summary>
    private void RefreshLocalizedComboBoxes()
    {
        _refreshingComboBoxes = true;

        RefreshSelectedIndex(CbTheme);
        RefreshSelectedIndex(CbTransparencyPreset);
        RefreshSelectedIndex(CbToastDuration);
        RefreshSelectedIndex(CbTouchLeftClick);
        RefreshSelectedIndex(CbTouchLeftDouble);
        RefreshSelectedIndex(CbTouchLeftTriple);
        RefreshSelectedIndex(CbTouchLeftSlide);
        RefreshSelectedIndex(CbTouchRightClick);
        RefreshSelectedIndex(CbTouchRightDouble);
        RefreshSelectedIndex(CbTouchRightTriple);
        RefreshSelectedIndex(CbTouchRightSlide);
        RefreshSelectedIndex(CbLanguage);

        _refreshingComboBoxes = false;
    }

    private static void RefreshSelectedIndex(ComboBox comboBox)
    {
        var idx = comboBox.SelectedIndex;
        if (idx >= 0)
        {
            comboBox.SelectedIndex = -1;
            comboBox.SelectedIndex = idx;
        }
    }

    private void ApplyTheme(int index)
    {
        var theme = SukiTheme.GetInstance();
        switch (index)
        {
            case 0:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Default);
                _isLightTheme = DetectSystemLightTheme();
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

    private static bool DetectSystemLightTheme()
    {
        var actualTheme = Application.Current?.ActualThemeVariant;
        return actualTheme == Avalonia.Styling.ThemeVariant.Light;
    }

    private IBrush GetCircleGray() => _isLightTheme ? _brushCircleGrayLight : _brushCircleGrayDark;

    private void RefreshThemeColors(bool refreshState = true)
    {
        // 清除之前可能残留的 SukiUI 资源覆盖，让 SukiUI 原生主题系统接管
        // （按钮、ComboBox 等控件的 Background 绑定到 SukiBackground，
        //   如果在 Window 级覆盖会导致按钮背景与窗口背景混为一体）
        Resources.Remove("SukiBackground");
        Resources.Remove("SukiCardBackground");

        EnsureThemeResourceBrushes();

        // 按原项目主题刷新顺序恢复窗口背景，避免侧栏透明层改变 Acrylic 的视觉层次。
        _windowBackgroundBrush.Color = _isLightTheme
            ? Color.FromRgb(0xE5, 0xE5, 0xEA)
            : Colors.Transparent;
        Background = _windowBackgroundBrush;

        var transparencyPct = ReadCardOpacity();
        var alpha = (byte)Math.Clamp(255 - (transparencyPct * 255 / 100), 25, 255); // 0→255, 90→26

        // 卡片背景：浅色使用白色半透；深色使用深色半透。Linux 下白色半透卡片容易呈现为白底。
        var cardBase = _isLightTheme ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        _glassCardBgBrush.Color = Color.FromArgb(alpha, cardBase.R, cardBase.G, cardBase.B);

        // 侧边栏底框与卡片背景保持一致，复用画刷对象。
        _sidebarBackgroundBrush.Color = Color.FromArgb(alpha, cardBase.R, cardBase.G, cardBase.B);
        SidebarBorder.Background = _sidebarBackgroundBrush;

        // 侧边栏选中高亮：浅色灰底，深色微白
        _sidebarSelectedBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x0C, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);

        // 弹窗遮罩：深色半透明黑 / 浅色半透明白
        _dialogOverlayBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x50, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x80, 0x00, 0x00, 0x00);

        // 普通文字按钮背景板：浅色用微黑，深色用微白，保留轻量层次感。
        _textPanelButtonBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        _textPanelButtonHoverBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x20, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        _textPanelButtonPressedBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x2A, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);

        // 重置对话框确认按钮颜色，避免主题切换后残留
        if (DialogConfirmBtn.IsVisible)
            DialogConfirmBtn.Background = Brushes.Transparent;

        HighlightAnc();
        if (_frontendState is not null)
        {
            StatusDot.Fill = _frontendState.Snapshot.IsConnected ? BrushGreen : BrushRed;
            StatusText.Foreground = _frontendState.Snapshot.IsConnected ? BrushLightGreen : BrushLightRed;
            if (refreshState)
                ApplyNextSnapshot(_frontendState.Snapshot);
        }
    }

    private void EnsureThemeResourceBrushes()
    {
        if (_themeResourceBrushesRegistered)
            return;

        Resources["GlassCardBg"] = _glassCardBgBrush;
        Resources["SidebarSelectedBg"] = _sidebarSelectedBgBrush;
        Resources["DialogOverlayBg"] = _dialogOverlayBgBrush;
        Resources["TextPanelButtonBg"] = _textPanelButtonBgBrush;
        Resources["TextPanelButtonHoverBg"] = _textPanelButtonHoverBgBrush;
        Resources["TextPanelButtonPressedBg"] = _textPanelButtonPressedBgBrush;
        _themeResourceBrushesRegistered = true;
    }

    private void ApplyTransparencyPreset(int idx)
    {
        if (idx <= 0) return;
        var card = idx switch
        {
            1 => 0,  // 清晰
            3 => 90, // 通透
            _ => 50, // 平衡
        };
        _applyingAppearancePreset = true;
        try
        {
            SlOpacity.Value = card;
        }
        finally
        {
            _applyingAppearancePreset = false;
        }
    }

    private void RefreshCardOpacity()
    {
        RefreshThemeColors(refreshState: false); // 只更新复用画刷颜色，不触发完整状态刷新链路
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
        WriteUiString("CustomName",
            string.IsNullOrWhiteSpace(TbCustomName.Text) ? null : TbCustomName.Text.Trim());
        UpdateTitle();
    }

    // 将 Next 设置中的主题名称转换为原界面的组合框索引。
    private int NextThemeIndex()
        => _uiSettings.GetString("Theme")?.ToLowerInvariant() switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

    // 将原界面的主题索引保存为稳定的 Next 设置值。
    private static string NextThemeName(int index) => index switch
    {
        1 => "Dark",
        2 => "Light",
        _ => "System"
    };

    // 读取 Next Toast 秒数并转换为原界面组合框索引。
    private int ReadToastDurationIndex()
    {
        var seconds = _uiSettings.GetInt("ToastDuration", 5);
        return seconds switch { 3 => 0, 4 => 1, 6 => 3, 7 => 4, 8 => 5, _ => 2 };
    }

    // 将 Toast 组合框索引转换为 Next 设置中的秒数。
    private static int ToastDurationSecondsFromIndex(int index) => index switch
    {
        0 => 3,
        1 => 4,
        3 => 6,
        4 => 7,
        5 => 8,
        _ => 5
    };

    // 读取统一的卡片透明度设置，保证主窗口和 Toast 使用同一值。
    private int ReadCardOpacity()
        => Math.Clamp(_uiSettings.GetInt("CardOpacity", 50), 0, 90);

    // 读取 Next 界面设置，避免窗口直接依赖设置对象结构。
    private bool ReadUiBool(string key, bool fallback)
        => _uiSettings.GetBool(key, fallback);

    private int ReadUiInt(string key, int fallback)
        => _uiSettings.GetInt(key, fallback);

    private void WriteUiBool(string key, bool value)
        => _uiSettings.SetBool(key, value);

    private void WriteUiString(string key, string? value)
        => _uiSettings.SetString(key, value);

    private void UpdateTitle()
    {
        var name = GetNextWindowTitle();
        Title = name;
        var snapshot = _frontendState?.Snapshot;
        var parts = new List<string>();
        AddNextBatteryPart(parts, "L", snapshot?.LeftBattery);
        AddNextBatteryPart(parts, "R", snapshot?.RightBattery);
        AddNextBatteryPart(parts, "C", snapshot?.CaseBattery);
    }

    // Next 未连接时显示应用名称，识别成功后才把耳机名称放到窗口标题。
    private string GetNextWindowTitle()
    {
        var snapshot = _frontendState?.Snapshot;
        if (snapshot?.IsConnected != true)
            return AppConst.WindowTitle;

        var custom = (TbCustomName.Text ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(custom)
            ? custom
            : DeviceText.DeviceName(snapshot.Identity?.DisplayName, snapshot.DeviceName, snapshot.Identity?.ModelName);
    }

    // 将 Next 快照中的电量拼接成托盘提示文本。
    private static void AddNextBatteryPart(List<string> parts, string channel, BatteryLevel? battery)
    {
        if (battery is { } value)
            parts.Add($"{channel}:{value.Percent}%{(value.IsCharging ? "⚡" : "")}");
    }

    // 将后端降噪模式模型转换为原版 ANC 控件使用的层级模型。
    private void ApplyNextNoiseSnapshot(BusinessSnapshot snapshot)
    {
        var manager = _controlManager?.ActiveManager;
        if (manager is null || !snapshot.IsConnected)
        {
            // 原项目始终保留降噪卡片，未连接时只显示占位提示。
            AncPanel.IsVisible = true;
            ResetNextAncUi();
            return;
        }

        var presentation = manager.Presentation;
        AncPanel.IsVisible = true;
        if (!presentation.SupportsNoiseCancellation)
        {
            ResetNextAncUi();
            return;
        }
        BuildAncUi(
            $"{snapshot.Identity?.ProductId}|{presentation.ModelName}",
            presentation.NoiseOptions);

        if (snapshot.Noise.Mode != NoiseMode.Unknown)
        {
            SyncAncFromState(manager.Presentation.CurrentNoiseModeKey);
            HighlightAnc();
        }

        // Smart 模式下显示设备实时计算出的当前档位，保持与原项目一致。
        if (snapshot.Noise.Mode == NoiseMode.Smart && snapshot.Noise.SmartLevel is { } smartLevel)
        {
            AncRealtimeHint.Text = string.Format(
                TranslationCatalog.Get("Anc_RealtimeHint"),
                DeviceText.NoiseModeName(smartLevel));
            AncRealtimeHint.IsVisible = true;
        }
        else
        {
            AncRealtimeHint.IsVisible = false;
        }
    }

    // 冻结游戏音效的中间回显，避免关闭互斥 EQ 时的过渡快照造成开关闪烁。
    private async Task RunGameSoundCommandAsync(bool enabled)
    {
        _gameSoundCommandPending = true;
        try
        {
            await (_commandDispatcher?.RunAsync("游戏音效", manager => manager.SetGameSoundEnabledAsync(enabled, CancellationToken.None)) ?? Task.FromResult(false));
        }
        finally
        {
            _gameSoundCommandPending = false;
            if (!_realClose && _frontendState is not null)
                await Dispatcher.UIThread.InvokeAsync(() => ApplyNextFeatureState(_frontendState.Snapshot));
        }
    }

    // 释放 Next ANC 动态控件，并清除型号缓存，保证同型号重连时重新生成按钮。
    private void ResetNextAncUi()
    {
        // 保留降噪卡片，仅清空其中的动态选项并显示占位内容。
        AncPanel.IsVisible = true;
        AncMainRow.Children.Clear();
        AncMainRow.ColumnDefinitions.Clear();
        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        AncSubRow.IsVisible = false;
        AncMainRow.IsVisible = false;
        AncPlaceholderText.IsVisible = true;
        AncRealtimeHint.IsVisible = false;
        _ancBuiltForModel = string.Empty;
        _ancSubSignature = string.Empty;
        _ancOptions = new List<NoiseOptionModel>();
        _ancMainButtons.Clear();
        _ancSubButtons.Clear();
        _ancChildToMain.Clear();
        _ancMain = string.Empty;
        _ancLevel = string.Empty;
    }

    private void NavHome_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("home");
    private void NavEq_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("eq");
    private void NavLog_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("log");
    private void NavPersonal_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("personal");
    private void NavSettings_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("settings");

    private void ShowPage(string page)
    {
        if (_currentPage != page)
        {
            _logManager?.Debug("UI", $"页面切换: {_currentPage} -> {page}");
            _currentPage = page;
        }
        MainPanel.IsVisible = page == "home";
        // 主页始终保留降噪卡片，未连接设备时由占位内容填充。
        if (page == "home")
            AncPanel.IsVisible = true;
        EqPanel.IsVisible = page == "eq";
        if (page == "eq")
            _ = _commandDispatcher?.RunAsync("刷新自定义 EQ", manager => manager.RefreshCustomEqualizersAsync(CancellationToken.None));
        DeviceInfoPanel.IsVisible = page == "deviceinfo";
        PersonalPanel.IsVisible = page == "personal";
        SettingsPanel.IsVisible = page == "settings";
        LogPanel.IsVisible = page == "log";
        AboutPanel.IsVisible = page == "about";

        NavHome.Classes.Remove("selected");
        NavEq.Classes.Remove("selected");
        NavPersonal.Classes.Remove("selected");
        NavSettings.Classes.Remove("selected");

        if (page == "home") NavHome.Classes.Add("selected");
        else if (page == "eq") NavEq.Classes.Add("selected");
        else if (page == "personal") NavPersonal.Classes.Add("selected");
        else NavSettings.Classes.Add("selected");

        if (page != "log")
        {
            _logRefreshTimer?.Stop();
            _logScrollPending = false;
            _logAutoScroll = true;
        }
        if (page == "deviceinfo" || page == "settings") RefreshDeviceInfo();
        if (page == "eq" && _frontendState is not null)
            ApplyNextEqualizerSnapshot(_frontendState.Snapshot);
        if (page == "log") RefreshLogView();
    }

    private void About_Click(object? s, RoutedEventArgs e) => ShowPage("about");
    private void AboutBack_Click(object? s, RoutedEventArgs e) => ShowPage("settings");

    /// <summary>平台适配：根据保存的设置，在构造阶段应用渲染管线配置。</summary>
    private void AdaptToPlatform()
    {
        if (ReadUiBool("AcrylicBlur", false))
        {
            TransparencyLevelHint = new List<WindowTransparencyLevel>
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Transparent
            };
            Background = Avalonia.Media.Brushes.Transparent;
            SidebarFullBg.IsVisible = true;
            SidebarBorder.Background = Avalonia.Media.Brushes.Transparent;

            if (OperatingSystem.IsWindows())
                BackgroundShaderCode = "vec4 main(vec2 fragCoord) { return vec4(0.0); }";
        }
        if (ReadUiBool("AdvancedRender", false))
            EnableAdvancedRender();
    }

    /// <summary>Acrylic 模糊开/关（仅保存设置，需重启生效）。</summary>
    private void ToggleAcrylicBlur(bool on)
    {
        WriteUiBool("AcrylicBlur", on);
        _logManager?.Debug("UI", $"设置: Acrylic 模糊 -> {on}");
        if (on)
            SelectBackground("default");
        UpdateBackgroundSettingsAvailability(on);

        ToastManager.CreateToast()
            .WithTitle(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabled)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabled))
            .WithContent(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabledMsg)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabledMsg))
            .Dismiss().After(TimeSpan.FromSeconds(3)).Queue();
    }

    private void UpdateBackgroundSettingsAvailability(bool acrylicBlurEnabled)
    {
        var enabled = !acrylicBlurEnabled;
        BackgroundSettingsContent.IsEnabled = enabled;
        BackgroundSettingsContent.Opacity = enabled ? 1 : 0.45;
        BgThumbDefault.Classes.Set("selected", true);
    }

    /// <summary>启用高级渲染：关 SukiWindow Chrome + 简易标题栏 + AcrylicBlur。</summary>
    private void EnableAdvancedRender()
    {
        IsTitleBarVisible = false;
        CustomTitleBar.IsVisible = true;
        SidebarFullBg.IsVisible = true;
        SidebarBorder.Background = Avalonia.Media.Brushes.Transparent;
        RootGrid.Margin = new Thickness(0, 32, 0, 0);
    }

    private void DisableAdvancedRender()
    {
        IsTitleBarVisible = true;
        CustomTitleBar.IsVisible = false;
        SidebarFullBg.IsVisible = false;
        SidebarBorder.Background = _sidebarBackgroundBrush;
        RootGrid.Margin = default;
    }

    private void TitleBarDrag_PointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e)
        => BeginMoveDrag(e);

    private void CustomMin_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "窗口操作: 最小化");
        WindowState = WindowState.Minimized;
    }

    private void CustomMax_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var nextState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        _logManager?.Debug("UI", $"窗口操作: 切换窗口状态 -> {nextState}");
        WindowState = nextState;
    }

    private void CustomClose_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "窗口操作: 点击关闭按钮");
        Close();
    }

    // ===== 背景设置 =====
    private void BtnBgLeft_Click(object? s, RoutedEventArgs e)
        => BgThumbScroller.Offset = BgThumbScroller.Offset.WithX(Math.Max(0, BgThumbScroller.Offset.X - 200));

    private void BtnBgRight_Click(object? s, RoutedEventArgs e)
        => BgThumbScroller.Offset = BgThumbScroller.Offset.WithX(
            Math.Min(BgThumbScroller.Extent.Width - BgThumbScroller.Viewport.Width,
                     BgThumbScroller.Offset.X + 200));

    private async Task BtnBgAdd_Click()
    {
        if (CbAcrylicBlur.IsChecked == true)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = LanguageManager.Instance.GetString(LanguageManager.Instance.ImagePicker_Title),
            FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
            {
                new(LanguageManager.Instance.GetString(LanguageManager.Instance.ImagePicker_FilterName)) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
            },
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
        {
            if (_backgroundSelection.Add(files[0].Path.LocalPath))
                RefreshBgThumbs();
        }
    }

    /// <summary>选中背景（default=默认, 路径=图片）。会高亮对应缩略图并实时应用到窗口。</summary>
    private void SelectBackground(string key)
    {
        if (CbAcrylicBlur.IsChecked == true && key != "default")
            return;

        _logManager?.Debug("UI", key == "default" ? "背景: 选择默认背景" : "背景: 选择自定义背景");
        _backgroundSelection.Select(key);
        BgThumbDefault.Classes.Set("selected", key == "default");
        foreach (var child in BgThumbList.Children)
        {
            Border? img = null;
            if (child is Border b && b != BgThumbDefault && b != BgThumbAdd)
                img = b;
            else if (child is Panel p && p.Children.Count > 0)
                img = p.Children[0] as Border;
            if (img != null)
                img.Classes.Set("selected", img.Tag as string == key);
        }

        ApplySavedBackground();
    }

    private void ApplySavedBackground()
    {
        var key = _backgroundSelection.SelectedKey;
        if (key == "default" || !_backgroundImages.IsAvailable(key))
        {
            SetSukiBackgroundStyle(SukiBackgroundStyle.Bubble);
            SetBackgroundImageSource(null, "");
            _backgroundImages.ClearBackgroundCache(keepKey: null);
            BgFullImage.IsVisible = false;
            return;
        }

        SetSukiBackgroundStyle(SukiBackgroundStyle.Flat);
        var blur = Math.Clamp(ReadUiInt("BgBlur", 0), 0, 20);
        var cacheKey = _backgroundImages.BuildCacheKey(key, GetBackgroundTargetWidth(), blur);
        SetBackgroundImageSource(
            _backgroundImages.GetOrCreateBitmap(key, GetBackgroundTargetWidth(), blur, cacheKey),
            cacheKey);
        _backgroundImages.ClearBackgroundCache(keepKey: cacheKey);
        BgFullImage.IsVisible = true;
    }

    private void SetBackgroundImageSource(IImage? source, string cacheKey)
    {
        var old = _backgroundImageSource;
        if (ReferenceEquals(old, source))
            return;

        BgFullImage.Source = source;
        _backgroundImageSource = source;
    }

    private int GetBackgroundTargetWidth()
    {
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : 900;
        // 限制背景处理尺寸，避免 2K/4K 图片在前台产生过大的 Skia/Avalonia/GPU 峰值。
        return Math.Clamp((int)Math.Ceiling(windowWidth * 1.1), 900, 1440);
    }

    private void SetSukiBackgroundStyle(SukiBackgroundStyle style)
    {
        if (BackgroundStyle != style)
            BackgroundStyle = style;
    }

    /// <summary>重建缩略图列表（默认 + 历史），添加按钮独立放在标题行。</summary>
    private void RefreshBgThumbs()
    {
        // 清除旧历史缩略图（保留默认缩略图）
        for (int i = BgThumbList.Children.Count - 1; i >= 0; i--)
        {
            var c = BgThumbList.Children[i];
            if ((c is Border b && b == BgThumbDefault) || c == null)
                continue;
            BgThumbList.Children.RemoveAt(i);
        }
        foreach (var path in _backgroundSelection.History)
        {
            var img = new Border
            {
                Width = 90, Height = 60, CornerRadius = new Avalonia.CornerRadius(8),
                Classes = { "bgThumb" }, Tag = path,
            };
            try
            {
                if (_backgroundImages.IsAvailable(path))
                    img.Background = new Avalonia.Media.ImageBrush(_backgroundImages.GetOrCreateThumbnail(path))
                    {
                        Stretch = Stretch.UniformToFill
                    };
                else
                    img.Background = Avalonia.Media.Brushes.DimGray;
            }
            catch { img.Background = Avalonia.Media.Brushes.DimGray; }
            img.PointerPressed += (_, _) => SelectBackground(path);

            // 删除按钮（悬停时出现在右上角）
            var delBtn = new Border
            {
                Width = 18, Height = 18, CornerRadius = new Avalonia.CornerRadius(9),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(200, 40, 40)),
                IsVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = "✕", FontSize = 11,
                    Foreground = Avalonia.Media.Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            delBtn.PointerPressed += (_, e) =>
            {
                e.Handled = true; // 阻止点击穿透
                if (_backgroundSelection.Remove(path))
                    RefreshBgThumbs();
            };

            var wrapper = new Panel { Width = 90, Height = 60, Cursor = img.Cursor };
            wrapper.Children.Add(img);
            wrapper.Children.Add(delBtn);
            wrapper.PointerEntered += (_, _) => delBtn.IsVisible = true;
            wrapper.PointerExited += (_, _) => delBtn.IsVisible = false;

            BgThumbList.Children.Add(wrapper);
        }
        SelectBackground(_backgroundSelection.SelectedKey);
    }

    private async void BtnFeedback_Click(object? s, RoutedEventArgs e)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackTitle);
        DialogMessage.FontSize = 13;
        DialogMessage.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackMessage);
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Cancel);
        DialogCancelBtn.IsVisible = true;
        DialogSkipBtn.Content = "GitLab";
        DialogSkipBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Confirm);
        DialogOverlay.IsVisible = true;

        var ok = await _confirmTcs.Task;
        if (!ok) return;

        ExportFeedback("https://github.com/Zhaoyi-ya/OppoPodsManager/issues/new");
    }

    private void ExportFeedback(string url)
    {
        if (_feedbackExporter is null)
            return;

        var result = _feedbackExporter.ExportToDesktop(
            VersionText.Text ?? "unknown",
            _frontendState?.Snapshot);
        if (!result.Succeeded)
            return;

        _desktopLinks?.TryOpen(url, "反馈链接");
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
            await ShowCheckResultDialog(
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackExported), result.FileName),
                LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackTitle)));
    }

    private void OpenUrl_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is string url)
            _desktopLinks?.TryOpen(url, "页面链接");
    }

    // ========== EQ 调节 ==========

    private string _eqCurrentPreset = "";
    private int _eqCurrentId; // 当前编辑的设备端预设 eqId，0=新建
    private bool _eqSuppressListEvent;
    private bool _nextEqEditing;
    private bool _synchronizingNextEq;
    private IReadOnlyList<ushort> _nextEqFrequencies = [];
    private sbyte _nextEqMinimumGain = BrandPresentation.DefaultCustomEqMinimumGain;
    private sbyte _nextEqMaximumGain = BrandPresentation.DefaultCustomEqMaximumGain;
    private readonly List<DynamicEqBand> _dynamicEqBands = [];

    // 保存动态 EQ 频段对应的滑块和数值标签。
    private sealed record DynamicEqBand(int Frequency, Slider Slider, TextBlock DbLabel);

    /// <summary>滑块值变更 → 更新对应 dB 标签，触发防抖预览下发。</summary>
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
        else
        {
            var dynamicBand = _dynamicEqBands.FirstOrDefault(band => ReferenceEquals(band.Slider, slider));
            if (dynamicBand is not null)
                dynamicBand.DbLabel.Text = text;
        }

        if (_synchronizingNextEq)
            return;

        // 防抖 150ms 后下发自定义 EQ（实时预览）。复用同一个 DispatcherTimer，避免拖动滑块时反复创建 Timer/闭包。
        var timer = EnsureEqDebounceTimer();
        timer.Stop();
        timer.Start();
    }

    // ---- 预设列表 ----

    /// <summary>刷新日志列表视图，始终保留完整后端和前端日志。</summary>
    private void RefreshLogView()
    {
        var lines = (_logManager ?? ApplicationLog.Current)?.Snapshot()
            .Select(entry => entry.ToString())
            .ToArray() ?? [];
        var version = lines.Length;
        if (version == _renderedLogVersion && _renderedLogEntries.Count > 0)
            return;

        _renderedLogEntries.Clear();
        foreach (var line in lines)
            _renderedLogEntries.Add(line);
        _renderedLogVersion = version;

        // 自动跟随最新日志
        if (_logAutoScroll && _logScrollViewer != null && !_logScrollPending)
        {
            _logScrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _logScrollPending = false;
                if (_logScrollViewer != null)
                    _logScrollViewer.Offset = new Vector(_logScrollViewer.Offset.X,
                        _logScrollViewer.Extent.Height);
            }, DispatcherPriority.Loaded);
        }
    }

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
        _eqCurrentPreset = item.Name;

        var manager = _controlManager?.ActiveManager;
        if (manager is null) return;
        if (sendToDevice)
            _ = _commandDispatcher?.RunAsync("EQ 预设", active => active.SetEqualizerByNameAsync(item.Name, CancellationToken.None));

        // 内置预设：直接生效，不显示滑块
        if (!item.IsCustom && !item.IsDeviceEntry)
        {
            EqSliderCard.IsVisible = false;
            EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSwitched), item.Name);
            // 同步主页调音下拉框（抑制事件避免循环）
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.SelectedItem = item.Name;
            CbEq.SelectionChanged += CbEq_SelectionChanged;
            return;
        }

        // 自定义/设备端预设：显示滑块编辑
        EqSliderCard.IsVisible = true;
        // 尝试加载设备保存的增益值
        var entry = _frontendState?.Snapshot.EqualizerEntries.FirstOrDefault(d => d.Name == item.Name);
        if (entry is { Gains.Count: > 0, Frequencies.Count: > 0 })
            ApplyNextEqEntry(entry);
        else
            SetAllEqSliders(0);
        _eqCurrentId = item.EqId;
        _logManager?.Debug("UI", $"EQ选中: name={item.Name} eqId={_eqCurrentId} isCustom={item.IsCustom} isDev={item.IsDeviceEntry}");
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintEditing), item.Name);
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

    /// <summary>新建/保存后立即在自定义列表中追加并选中，不等设备响应。</summary>
    private void AddCustomPresetToList(string name)
    {
        // 避免重复——已有同名项则只选中不追加
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
            if (item.Name == name) { LbEqCustomPresets.SelectedItem = item; return; }

        var newItem = new EqPresetItem { Name = name, DisplayName = name, IsCustom = true, EqId = 0 };
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
        EqSlider62.Value = value;
        EqSlider250.Value = value;
        EqSlider1k.Value = value;
        EqSlider4k.Value = value;
        EqSlider8k.Value = value;
        EqSlider16k.Value = value;
        foreach (var band in _dynamicEqBands)
            band.Slider.Value = Math.Clamp(value, band.Slider.Minimum, band.Slider.Maximum);
    }

    // 将 Next 后端的 EQ 条目按型号频率写回原版滑块。
    private void ApplyNextEqEntry(EqualizerEntrySnapshot entry)
    {
        var manager = _controlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        var frequencies = presentation?.CustomEqFrequencies.ToArray() ?? [];
        if (manager is null || frequencies.Length == 0)
        {
            ConfigureNextEqBands([], entry.MinimumGain, entry.MaximumGain);
            return;
        }

        _nextEqMinimumGain = entry.MinimumGain;
        _nextEqMaximumGain = entry.MaximumGain;
        var gains = manager.AlignCustomEqualizerGains(entry);
        ConfigureNextEqBands(frequencies, _nextEqMinimumGain, _nextEqMaximumGain, gains);
    }

    // 按官方白名单频率数量选择原版六段控件或动态 EQ 控件。
    private void ConfigureNextEqBands(
        IReadOnlyList<ushort> frequencies,
        sbyte minimumGain,
        sbyte maximumGain,
        IReadOnlyList<sbyte>? gains = null)
    {
        var normalizedFrequencies = frequencies.ToArray();
        _nextEqFrequencies = normalizedFrequencies;
        _nextEqMinimumGain = minimumGain;
        _nextEqMaximumGain = maximumGain;
        var useFixedBands = normalizedFrequencies is [62, 250, 1000, 4000, 8000, 16000];

        EqFixedBandsGrid.IsVisible = useFixedBands;
        EqDynamicBandsGrid.IsVisible = !useFixedBands;
        _dynamicEqBands.Clear();
        EqDynamicBandsGrid.Children.Clear();
        EqDynamicBandsGrid.ColumnDefinitions.Clear();
        if (normalizedFrequencies.Length == 0)
            return;
        if (useFixedBands)
        {
            ConfigureFixedEqSliders(minimumGain, maximumGain, gains);
            return;
        }

        EqDynamicBandsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var scale = new Grid
        {
            Height = 180,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var maximumLabel = new TextBlock { Text = $"+{maximumGain}", FontSize = 10, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Right };
        var zeroLabel = new TextBlock { Text = "0", FontSize = 10, Opacity = 0.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var minimumLabel = new TextBlock { Text = minimumGain.ToString(), FontSize = 10, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(zeroLabel, 1);
        Grid.SetRow(minimumLabel, 2);
        scale.Children.Add(maximumLabel);
        scale.Children.Add(zeroLabel);
        scale.Children.Add(minimumLabel);
        EqDynamicBandsGrid.Children.Add(scale);

        for (var index = 0; index < normalizedFrequencies.Length; index++)
        {
            var frequency = normalizedFrequencies[index];
            var dbLabel = new TextBlock
            {
                Text = "0",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 0, 2)
            };
            var slider = new Slider
            {
                Width = 36,
                Height = 180,
                Orientation = Orientation.Vertical,
                Minimum = minimumGain,
                Maximum = maximumGain,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = gains is not null && index < gains.Count ? gains[index] : 0
            };
            slider.PropertyChanged += EqSlider_Changed;
            var frequencyLabel = new TextBlock
            {
                Text = FormatEqFrequency(frequency),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(dbLabel);
            panel.Children.Add(slider);
            panel.Children.Add(frequencyLabel);
            EqDynamicBandsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(panel, index + 1);
            EqDynamicBandsGrid.Children.Add(panel);
            _dynamicEqBands.Add(new DynamicEqBand(frequency, slider, dbLabel));
            UpdateEqDbLabel(slider, dbLabel);
        }
    }

    // 配置原项目六段 EQ 控件的协议范围和当前增益。
    private void ConfigureFixedEqSliders(sbyte minimumGain, sbyte maximumGain, IReadOnlyList<sbyte>? gains)
    {
        var sliders = new[] { EqSlider62, EqSlider250, EqSlider1k, EqSlider4k, EqSlider8k, EqSlider16k };
        for (var index = 0; index < sliders.Length; index++)
        {
            sliders[index].Minimum = minimumGain;
            sliders[index].Maximum = maximumGain;
            sliders[index].Value = gains is not null && index < gains.Count ? gains[index] : 0;
        }
    }

    // 更新动态 EQ 滑块顶部的 dB 文本。
    private static void UpdateEqDbLabel(Slider slider, TextBlock label)
    {
        var db = (int)Math.Round(slider.Value);
        label.Text = db > 0 ? $"+{db}" : db.ToString();
    }

    // 将协议频率转换为与原项目一致的简短标签。
    private static string FormatEqFrequency(ushort frequency) => frequency >= 1000 && frequency % 1000 == 0
        ? $"{frequency / 1000}k"
        : frequency.ToString();

    // 从固定或动态 EQ 控件读取当前增益数组。
    private IReadOnlyList<double> ReadNextEqGains()
    {
        if (_dynamicEqBands.Count > 0)
            return _dynamicEqBands.Select(band => band.Slider.Value).ToArray();

        return new[] { EqSlider62, EqSlider250, EqSlider1k, EqSlider4k, EqSlider8k, EqSlider16k }
            .Select(slider => slider.Value)
            .ToArray();
    }

    // 从原版滑块值构造 Next 控制层可写入的 EQ 条目。
    private EqualizerEntrySnapshot? BuildNextEqEntry(byte id, string name)
    {
        var manager = _controlManager?.ActiveManager;
        if (manager is null)
            return null;

        var gains = ReadNextEqGains();
        return manager.CreateCustomEqualizerEntry(id, name, gains);
    }

    /// <summary>向设备发送当前 UI 滑块值作为自定义 EQ 预览/更新。</summary>
    private void SendCurrentCustomEq()
    {
        if (_eqCurrentId <= 0 || string.IsNullOrWhiteSpace(_eqCurrentPreset))
            return;
        var entry = BuildNextEqEntry((byte)_eqCurrentId, _eqCurrentPreset);
        if (entry is not null)
            _ = _commandDispatcher?.RunAsync("EQ 预览", manager => manager.PreviewCustomEqualizerAsync(entry, CancellationToken.None));
    }

    // ---- 按钮操作 ----

    private void BtnEqCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 仅当前编辑自定义/设备端预设时生效
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        SetAllEqSliders(0);
        EqHintText.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintReset);
    }

    private async void BtnEqNew_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var manager = _controlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        if (manager is null || presentation is null || !presentation.SupportsCustomEqualizer)
            return;

        string? name;
        do
        {
            name = await ShowPromptDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InputPresetName),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_Custom),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName));
            if (string.IsNullOrEmpty(name)) return;
            if (manager.IsValidCustomEqualizerName(name)) break;
            await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName), LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidNameTitle));
        } while (true);
        _eqCurrentPreset = name;
        _eqCurrentId = 0;
        _nextEqEditing = true;
        var nextPresentation = _controlManager?.ActiveManager?.Presentation;
        if (nextPresentation is not null)
        {
            // 新建时立即按当前型号白名单创建全部频段，不能只清零原有固定滑块。
            ConfigureNextEqBands(
                nextPresentation.CustomEqFrequencies,
                nextPresentation.CustomEqMinimumGain,
                nextPresentation.CustomEqMaximumGain);
        }
        SetAllEqSliders(0);
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintNewPreset), name);
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.SelectedItem = null;

        // 立即加入自定义列表并选中
        AddCustomPresetToList(name);
    }

    private void BtnEqSave_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var manager = _controlManager?.ActiveManager;
        if (manager is null
            || string.IsNullOrEmpty(_eqCurrentPreset)
            || !manager.IsValidCustomEqualizerName(_eqCurrentPreset))
            return;
        var entry = BuildNextEqEntry((byte)_eqCurrentId, _eqCurrentPreset);
        if (entry is null)
            return;
        _ = SaveNextEqualizerAsync(entry);
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSaved), _eqCurrentPreset);
    }

    private async void EqListItemDelete_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not string name) return;
        if (!await ShowConfirmDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ConfirmDelete),
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_DeleteConfirm), name))) return;

        var nextEntry = _frontendState?.Snapshot.EqualizerEntries.FirstOrDefault(entry => entry.Name == name);
        if (nextEntry is null)
            return;
        _ = _commandDispatcher?.RunAsync("EQ 删除", manager => manager.DeleteCustomEqualizerAsync(nextEntry, CancellationToken.None));

        if (_eqCurrentPreset == name)
        {
            _eqCurrentPreset = "";
            SetAllEqSliders(0);
        }
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintDeleted), name);
    }

    // ---- 设备详情 ----

    /// <summary>刷新设备详情页（固件、编解码器）。</summary>
    private void RefreshDeviceInfo()
    {
        var snapshot = _frontendState?.Snapshot;
        DiDeviceName.Text = DeviceText.DeviceName(snapshot?.Identity?.ModelName, snapshot?.DeviceName);
        DiFirmware.Text = snapshot?.Identity?.FirmwareVersion ?? "-";
        DiCodec.Text = snapshot?.Identity?.Codec ?? "-";
        _logManager?.Debug("UI", "刷新 Next 设备详情。");
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
        DialogMessage.Text = string.IsNullOrEmpty(hint) ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InputPresetName) : hint;
        DialogInput.IsVisible = true;
        DialogInput.Text = defaultText;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Cancel);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Save);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        _logManager?.Debug("UI", $"对话框: 打开输入框 -> {title}");
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
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Cancel);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ConfirmDelete);
        DialogConfirmBtn.Background = new SolidColorBrush(Color.Parse("#CCE81123"));
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        _logManager?.Debug("UI", $"对话框: 打开确认框 -> {title}");

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
        _updateCoordinator?.SkipVersion(_updatePendingVersion);
        DialogOverlay_Close();
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogOverlay_Close()
    {
        DialogOverlay.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogMirrorBtn.IsVisible = false;
    }

    private void DialogMirror_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 国内下载");
        DialogOverlay_Close();
        _confirmTcs?.TrySetResult(false);
        _updateCoordinator?.TryOpenDownload("mirror", UpdateCoordinator.MirrorDownloadUrl);
    }

    private void DialogCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 取消");
        DialogOverlay_Close();
        _promptTcs?.TrySetResult(null);
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogConfirm_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 确认");
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
        _logManager?.Debug("UI", "用户操作: 点击重连");
        _ = ConnectNextDeviceAsync(null);
    }

    // 通过控制层重建当前设备会话，界面不接触蓝牙或协议细节。
    private async Task ConnectNextDeviceAsync(string? deviceId)
    {
        if (_controlManager is null)
            return;

        if (string.IsNullOrWhiteSpace(deviceId))
            await _controlManager.ConnectFirstAvailableAsync(CancellationToken.None);
        else
            await _controlManager.ConnectAsync(deviceId, CancellationToken.None);
    }

    // 保存完成后切换到设备端条目，再解除编辑保护并刷新界面。
    private async Task SaveNextEqualizerAsync(EqualizerEntrySnapshot entry)
    {
        try
        {
            await (_commandDispatcher?.RunAsync("EQ 保存", manager => manager.SaveCustomEqualizerAsync(entry, CancellationToken.None)) ?? Task.FromResult(false));
        }
        finally
        {
            _nextEqEditing = false;
            if (!_realClose && _frontendState is not null)
                await Dispatcher.UIThread.InvokeAsync(() => ApplyNextEqualizerSnapshot(_frontendState.Snapshot));
        }
    }

    // 启动后读取控制层的设备选择项，不在窗口内扫描或探测蓝牙设备。
    private async Task RefreshNextDevicesAsync()
    {
        if (_controlManager is null)
            return;

        var devices = await _controlManager.RefreshAvailableDevicesAsync(CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyNextDevices(devices));
    }

    private void OnWindowClosing(object? s, WindowClosingEventArgs e)
    {
        if (_realClose)
            return;

        // 未启用关闭到托盘时，主窗口关闭请求直接交给应用生命周期处理。
        if (_shouldKeepWindowAlive?.Invoke() != true && _requestApplicationExit is not null)
        {
            e.Cancel = true;
            _realClose = true;
            DisposeRuntimeUiResources();
            _requestApplicationExit();
            return;
        }

        // 启用关闭到托盘时，关闭主窗口本身但保留托盘和设备会话。
        _realClose = true;
        DisposeRuntimeUiResources();
    }

    private void DisposeRuntimeUiResources()
    {
        if (_runtimeUiDisposed)
            return;
        _runtimeUiDisposed = true;

        Closing -= OnWindowClosing;
        if (_frontendState is not null)
            _frontendState.Changed -= OnNextStateChanged;
        if (_controlManager is not null)
            _controlManager.AvailableDevicesChanged -= OnAvailableDevicesChanged;
        PropertyChanged -= OnWindowPropertyChanged;
        _interactiveSurface?.Dispose();
        _interactiveSurface = null;
        foreach (var subscription in _linguaSubs)
            subscription.Dispose();
        _linguaSubs.Clear();
        _eqDebounceTimer?.Stop();
        _bgApplyDebounceTimer?.Stop();
        _logRefreshTimer?.Stop();
        DisposeWindowImages();
        SetBackgroundImageSource(null, "");
        _backgroundImages.Dispose();
    }

    // 调用 SukiWindow 自带的释放流程，清理 Toast host 和窗口渲染资源。
    private void DisposeSukiWindow()
    {
        if (_sukiWindowDisposed)
            return;

        _sukiWindowDisposed = true;
        Dispose();
    }

    // 保存个性化耳机图片卡片中的预览控件。
    private readonly Dictionary<EarphoneSlot, Image> _earphonePreviews = new();

    // 刷新主页和设备详情中的耳机图片资源。
    private void RefreshEarphoneImages()
    {
        ReplaceEarphoneImage(LeftBatteryImage, EarphoneSlot.HomeLeft);
        ReplaceEarphoneImage(RightBatteryImage, EarphoneSlot.HomeRight);
        ReplaceEarphoneImage(CaseBatteryImage, EarphoneSlot.Case);
        ReplaceEarphoneImage(DiTouchLeftImage, EarphoneSlot.HomeLeft);
        ReplaceEarphoneImage(DiTouchRightImage, EarphoneSlot.HomeRight);
    }

    // 替换图片前释放旧的独立位图，保留资源层共享位图不被误释放。
    private static void ReplaceEarphoneImage(Image image, EarphoneSlot slot)
    {
        var old = image.Source;
        image.Source = EarphoneImageProvider.GetBitmap(slot);
        if (old is Bitmap oldBitmap && !AssetHelper.IsShared(oldBitmap))
            oldBitmap.Dispose();
    }

    // 重建个性化耳机图片设置项，使语言切换后文案立即更新。
    private void BuildEarphoneCustomUi()
    {
        foreach (var preview in _earphonePreviews.Values)
            DisposeEarphoneImage(preview);

        EarphoneCustomContent.Children.Clear();
        _earphonePreviews.Clear();
        foreach (var slot in new[] { EarphoneSlot.Case, EarphoneSlot.HomeLeft, EarphoneSlot.HomeRight, EarphoneSlot.SmallDual })
        {
            var preview = new Image
            {
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
                Source = EarphoneImageProvider.GetBitmap(slot)
            };
            _earphonePreviews[slot] = preview;
            EarphoneCustomContent.Children.Add(new StackPanel
            {
                Width = 108,
                Spacing = 6,
                Margin = new Thickness(8, 4),
                Children =
                {
                    new Border
                    {
                        Width = 56,
                        Height = 56,
                        CornerRadius = new CornerRadius(8),
                        Background = _textPanelButtonBgBrush,
                        Child = preview
                    },
                    new TextBlock
                    {
                        Text = LanguageManager.Instance.GetString(slot switch
                        {
                            EarphoneSlot.HomeLeft => LanguageManager.Instance.Personal_EarphoneLeft,
                            EarphoneSlot.HomeRight => LanguageManager.Instance.Personal_EarphoneRight,
                            EarphoneSlot.SmallDual => LanguageManager.Instance.Personal_EarphoneDual,
                            _ => LanguageManager.Instance.Personal_EarphoneCase
                        }),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = BrushGray
                    }
                }
            });
        }
    }

    // 清理个性化设置项移除的独立位图资源。
    private static void DisposeEarphoneImage(Image image)
    {
        if (image.Source is Bitmap bitmap && !AssetHelper.IsShared(bitmap))
            bitmap.Dispose();
        image.Source = null;
    }

    // 关闭主窗口时释放首页和设备详情页持有的独立耳机位图。
    private void DisposeWindowImages()
    {
        DisposeEarphoneImage(LeftBatteryImage);
        DisposeEarphoneImage(RightBatteryImage);
        DisposeEarphoneImage(CaseBatteryImage);
        DisposeEarphoneImage(DiTouchLeftImage);
        DisposeEarphoneImage(DiTouchRightImage);
        foreach (var preview in _earphonePreviews.Values)
            DisposeEarphoneImage(preview);
        _earphonePreviews.Clear();
        EarphoneCustomContent.Children.Clear();
    }

    // ===== 设备列表 =====
    // 保存多设备行的可复用控件，状态刷新时只更新内容，避免反复重建视觉树。
    private readonly Dictionary<string, DeviceListRowRefs> _deviceListRows = new();

    private sealed class DeviceListRowRefs
    {
        public required Border Root { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock AudioText { get; init; }
        public required TextBlock StatusText { get; init; }
    }

    private void CloseFloatingMenusOnBlankClick(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;

        if (IsInsideFloatingMenuTrigger(source))
            return;

        CloseOpenComboBoxes();
        CloseOpenDeviceContextMenus();
    }

    private static bool IsInsideFloatingMenuTrigger(Visual source)
    {
        foreach (var visual in source.GetSelfAndVisualAncestors())
        {
            if (visual is ComboBox or ComboBoxItem or MenuItem or Avalonia.Controls.ContextMenu)
                return true;
        }

        return false;
    }

    private void CloseOpenComboBoxes()
    {
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
            comboBox.IsDropDownOpen = false;
    }

    private void CloseOpenDeviceContextMenus()
    {
        foreach (var row in _deviceListRows.Values)
        {
            if (row.Root.ContextMenu is { } menu && menu.IsOpen)
                menu.Close();
        }
    }

    // 多设备列表由 Next 快照统一刷新，界面只负责呈现和转发操作意图。

    // 使用 Next 快照刷新多设备列表，界面只负责呈现和转发操作意图。
    private void SyncNextMultiDeviceList(BusinessSnapshot? snapshot)
    {
        var manager = _controlManager?.ActiveManager;
        var connected = snapshot?.IsConnected == true;
        var hiddenCount = _controlManager?.GetHiddenMultiDeviceCount() ?? 0;
        var displayState = _controlManager?.GetMultiDeviceDisplayState()
            ?? new MultiDeviceDisplayState([], []);
        var devices = displayState.VisibleDevices;

        DeviceList.Items.Clear();
        _deviceListRows.Clear();
        DeviceListEmptyHint.Text = !connected
            ? LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_EmptyHint)
                : devices.Count == 0
                ? LanguageManager.Instance.GetString(hiddenCount > 0
                    ? LanguageManager.Instance.MultiDevice_AllHidden
                    : LanguageManager.Instance.MultiDevice_NoOtherDevices)
                : string.Empty;
        DeviceListEmptyHint.IsVisible = !connected || devices.Count == 0;
        DiConnectionStrategyCard.IsVisible = connected && devices.Count > 0;
        PriorityDevicePanel.IsVisible = connected && devices.Count > 0;
        if (!connected || manager is null)
            return;

        SyncNextConnectionStrategy(manager, snapshot!.MultiDevice, displayState.ConnectedDevices);

        foreach (var device in devices)
        {
            var name = DeviceText.MultiDeviceName(device);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3) };
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = device.ConnectionState switch { 2 => BrushGreen, 1 => BrushGray, _ => BrushRed }
            };
            var nameText = new TextBlock
            {
                Text = name,
                FontSize = 13,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = device.IsCurrent ? BrushLightGreen : BrushWhite,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(dot);
            row.Children.Add(nameText);
            var border = new Border
            {
                Padding = new Thickness(8, 5),
                CornerRadius = new CornerRadius(4),
                Child = row,
                Background = device.IsCurrent ? _deviceCurrentBgBrush : null
            };

            var audioText = new TextBlock { IsVisible = device.IsAudioActive, Text = " ♪", Foreground = BrushGreen };
            var statusText = new TextBlock
            {
                IsVisible = device.ConnectionState != 2 && !device.IsCurrent,
                Text = GetDeviceConnectionText(device),
                Opacity = 0.4
            };
            row.Children.Add(audioText);
            row.Children.Add(statusText);
            _deviceListRows[device.Address] = new DeviceListRowRefs { Root = border, Dot = dot, NameText = nameText, AudioText = audioText, StatusText = statusText };
            var menu = new ContextMenu();
            if (!device.IsCurrent && device.ConnectionState != 2)
                AddNextMultiDeviceMenuItem(menu, LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Connect), MultiDeviceOperation.Connect, device.Address, name);
            else if (device.ConnectionState == 2)
                AddNextMultiDeviceMenuItem(menu, LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Disconnect), MultiDeviceOperation.Disconnect, device.Address, name);
            if (!device.IsCurrent)
            {
                AddNextMultiDeviceMenuItem(menu, LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Unpair), MultiDeviceOperation.Unpair, device.Address, name);
                var hide = new MenuItem { Header = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Hide) };
                hide.Click += (_, _) => HideMultiDevice(device);
                menu.Items.Add(hide);
            }
            if (menu.Items.Count > 0)
                border.ContextMenu = menu;
            DeviceList.Items.Add(border);
        }

    }

    // 用 Next 业务快照填充原版优先设备控件。
    private void SyncNextConnectionStrategy(
        IBrandManager manager,
        MultiDeviceSnapshot multiDevice,
        IReadOnlyList<ConnectedDeviceSnapshot> connectedDevices)
    {
        var canManage = manager.Presentation.CanManageMultiDevice;
        DiConnectionStrategyCard.IsVisible = canManage;
        PriorityDevicePanel.IsVisible = canManage;
        _syncingConnectionStrategy = true;
        try
        {
            if (!canManage)
            {
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.SelectedItem = null;
                _priorityOptionsSignature = "";
                return;
            }

            var signature = string.Join("|", connectedDevices.Select(device =>
                $"{device.Address};{device.Name};{device.IsCurrent}"))
                + $"|auto={multiDevice.IsAutomaticPriority}|priority={multiDevice.PriorityDeviceAddress}";
            if (signature != _priorityOptionsSignature)
            {
                _priorityOptionsSignature = signature;
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.Items.Add(new PriorityDeviceOption
                {
                    IsAutomatic = true,
                    DisplayName = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Automatic)
                });
                foreach (var device in connectedDevices)
                    CbPriorityDevice.Items.Add(new PriorityDeviceOption
                    {
                        Address = device.Address,
                        DisplayName = DeviceText.MultiDeviceName(device)
                    });
            }

            var selected = multiDevice.IsAutomaticPriority
                ? CbPriorityDevice.Items.OfType<PriorityDeviceOption>().FirstOrDefault(option => option.IsAutomatic)
                : CbPriorityDevice.Items.OfType<PriorityDeviceOption>().FirstOrDefault(option =>
                    string.Equals(option.Address, multiDevice.PriorityDeviceAddress, StringComparison.OrdinalIgnoreCase));
            CbPriorityDevice.SelectedItem = selected;
            // Next 快照已经给出最终选择，不保留旧路径的延迟选择状态。
            CbPriorityDevice.PlaceholderText = selected is null && !multiDevice.IsAutomaticPriority
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityUnavailable)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityHint);
        }
        finally
        {
            _syncingConnectionStrategy = false;
        }
    }

    // 创建多设备菜单项并把动作交给控制层执行。
    private void AddNextMultiDeviceMenuItem(ContextMenu menu, string resourceKey, MultiDeviceOperation operation, string address, string name)
    {
        var item = new MenuItem { Header = string.Format(resourceKey, name) };
        item.Click += (_, _) => _ = _commandDispatcher?.RunAsync("多设备操作", manager => manager.OperateMultiDeviceAsync(operation, address, CancellationToken.None));
        menu.Items.Add(item);
    }

    private void CbPriorityDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CbPriorityDevice.SelectedItem is not PriorityDeviceOption option
            || _frontendState?.Snapshot.IsConnected != true)
            return;

        if (_syncingConnectionStrategy)
        {
            return;
        }

        ApplyPrioritySelection(option);
    }

    private void ApplyPrioritySelection(PriorityDeviceOption option)
    {
        _ = _commandDispatcher?.RunAsync("多设备优先级", manager =>
            manager.SetMultiDevicePriorityAsync(
                option.IsAutomatic,
                option.IsAutomatic ? null : option.Address,
                CancellationToken.None));
    }

    private void HideMultiDevice(ConnectedDeviceSnapshot device)
    {
        if (device.IsCurrent || string.IsNullOrWhiteSpace(device.Address)) return;
        if (_controlManager?.HideMultiDevice(device.Address) != true)
            return;
        SyncNextMultiDeviceList(_frontendState?.Snapshot);
        RefreshRestoreHiddenDevicesButton();
        _logManager?.Debug("UI", $"本地隐藏多设备 addr={device.Address}");
    }

    private void RefreshRestoreHiddenDevicesButton()
    {
        var count = _controlManager?.GetHiddenMultiDeviceCount() ?? 0;
        BtnRestoreHiddenDevices.IsEnabled = count > 0;
        BtnRestoreHiddenDevices.Content = count > 0
            ? string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_RestoreHidden), count)
            : LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_RestoreHiddenDevices);
    }

    private void BtnRestoreHiddenDevices_Click(object? sender, RoutedEventArgs e)
    {
        _controlManager?.RestoreHiddenMultiDevices();
        SyncNextMultiDeviceList(_frontendState?.Snapshot);
        RefreshRestoreHiddenDevicesButton();
        _ = _commandDispatcher?.RunAsync("刷新多设备列表", manager => manager.RefreshMultiDeviceAsync(CancellationToken.None));
        _logManager?.Debug("UI", "已清除本地隐藏设备策略并同步多设备状态");
    }

    private string GetDeviceConnectionText(ConnectedDeviceSnapshot d)
    {
        var obs = d.ConnectionState switch
        {
            2 => d.IsCurrent ? LanguageManager.Instance.MultiDevice_StatusCurrentDevice : LanguageManager.Instance.MultiDevice_StatusConnected,
            1 => LanguageManager.Instance.MultiDevice_StatusConnecting,
            _ => LanguageManager.Instance.MultiDevice_StatusDisconnected
        };
        return $" ({LanguageManager.Instance.GetString(obs)})";
    }




    /// <summary>切换语言后，用实时本地化标签刷新已生成的空间音频单选项文字。</summary>
    private void RefreshSpatialAudioLabels()
    {
        foreach (var c in SpatialAudioModes.Children)
        {
            if (c is not RadioButton rb || rb.Tag is not string mode) continue;
            rb.Content = mode switch
            {
                "Fixed" => LanguageManager.Instance.GetString(LanguageManager.Instance.SpatialAudio_ModeFixed),
                "Track" => LanguageManager.Instance.GetString(LanguageManager.Instance.SpatialAudio_ModeHeadTrack),
                _ => LanguageManager.Instance.GetString(LanguageManager.Instance.SpatialAudio_ModeOff),
            };
        }
    }

    // ===== 版本更新检查 =====

    private async void BtnCheckUpdate_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "用户操作: 手动检查更新");
        BtnCheckUpdate.IsEnabled = false;
        BtnCheckUpdate.Content = _checking;
        try { await DoCheckUpdateAsync(silent: false); }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = _checkUpdate;
        }
    }

    private void BtnViewLog_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "日志面板: 打开");
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

    // 将统一日志导出为 ZIP 文件，便于提交诊断信息。
    private async void BtnLogExport_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave)
            return;

        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportTitle),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            SuggestedFileName = $"OPPOPods_logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        });
        if (file is null)
            return;

        var log = _logManager ?? ApplicationLog.Current;
        if (log is null)
            return;

        if (!log.TryExportZip(file.Path.LocalPath, out var exportError))
        {
            await ShowCheckResultDialog(
                string.Format(
                    LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ExportError),
                    exportError ?? string.Empty),
                LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportZip));
            return;
        }

        await ShowCheckResultDialog(
            string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ExportSuccess), file.Path.LocalPath),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportZip));
    }

    private void BtnLogBack_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "日志面板: 返回设置");
        _logRefreshTimer?.Stop();
        _logAutoScroll = true;
        LogPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }
    private async Task DoCheckUpdateAsync(bool silent = false)
    {
        if (_updateCoordinator is null)
        {
            _logManager?.Debug("UI", "检查更新跳过：更新协调器尚未注入。");
            return;
        }

        // 计算当前界面文化，交给更新服务请求本地化的更新说明。
        var uiLang = LanguageManager.ResolveCulture(_uiSettings.GetString("Language")).Name;
        var result = await _updateCoordinator.CheckAsync(
            VersionText.Text ?? "unknown",
            uiLang,
            CancellationToken.None,
            respectSkippedVersion: silent);

        if (result.Status is UpdateCheckStatus.Canceled or UpdateCheckStatus.Skipped)
            return;

        if (result.Status == UpdateCheckStatus.UpToDate)
        {
            if (!silent)
                await ShowCheckResultDialog(string.Format(
                    LanguageManager.Instance.GetString(LanguageManager.Instance.Update_UpToDate),
                    VersionText.Text));
            return;
        }

        if (!result.IsAvailable || string.IsNullOrWhiteSpace(result.Version))
        {
            if (!silent)
                await ShowCheckResultDialog(GetUpdateFailureText(result.Status));
            return;
        }

        var serverVersion = result.Version;
        if (!silent)
        {
            var go = await ShowUpdateDialog(serverVersion, result.Content);
            if (go)
                _updateCoordinator.TryOpenDownload("github", result.DownloadUrl);
            return;
        }

        var shouldUseToast = !IsVisible || WindowState == WindowState.Minimized || !IsActive;
        if (shouldUseToast)
        {
            var action = await ToastWindow.ShowUpdateAsync(serverVersion);
            HandleUpdateToastAction(action, serverVersion, result.DownloadUrl);
        }
        else
        {
            var go = await ShowUpdateDialog(serverVersion, result.Content);
            if (go)
                _updateCoordinator.TryOpenDownload("github", result.DownloadUrl);
        }
    }

    // 将更新服务状态转换为当前语言的界面提示文本。
    private static string GetUpdateFailureText(UpdateCheckStatus status)
        => LanguageManager.Instance.GetString(status switch
        {
            UpdateCheckStatus.Timeout => LanguageManager.Instance.Update_Timeout,
            UpdateCheckStatus.NetworkError => LanguageManager.Instance.Update_ConnectFailed,
            UpdateCheckStatus.ParseError => LanguageManager.Instance.Update_ParseError,
            _ => LanguageManager.Instance.Update_NetworkError
        });

    private void HandleUpdateToastAction(UpdateToastAction action, string serverVersion, string downloadUrl)
    {
        if (action == UpdateToastAction.Skip)
        {
            _updateCoordinator?.SkipVersion(serverVersion);
            return;
        }

        if (action == UpdateToastAction.MirrorDownload)
        {
            _updateCoordinator?.TryOpenDownload("mirror", UpdateCoordinator.MirrorDownloadUrl);
            return;
        }

        if (action == UpdateToastAction.Download)
        {
            _updateCoordinator?.TryOpenDownload("github", downloadUrl);
        }
    }

    private async Task ShowCheckResultDialog(string msg, string? title = null)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = title ?? LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_CheckUpdate);
        DialogMessage.Text = msg;
        DialogInput.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogCancelBtn.IsVisible = false;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_OK);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        await _confirmTcs.Task;
    }

    private async Task<bool> ShowUpdateDialog(string newVersion, string content = "")
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Toast_NewVersion);
        if (string.IsNullOrEmpty(content))
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageNoContent), newVersion, VersionText.Text);
        else
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageWithContent), newVersion, VersionText.Text) + content;
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_RemindLater);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogSkipBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_SkipVersion);
        DialogSkipBtn.Background = Brushes.Transparent;
        DialogSkipBtn.IsVisible = true;
        DialogMirrorBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_MirrorDownload);
        DialogMirrorBtn.Background = Brushes.Transparent;
        DialogMirrorBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_GitHubDownload);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        _updatePendingVersion = newVersion;

        return await _confirmTcs.Task;
    }

    // 查找耳机安全警告（与“更新提醒”同款的软件内模态浮层 DialogOverlay）。
    // 仅“启动查找”时调用，返回 true 表示用户已知晓风险并继续。
    private async Task<bool> ShowFindWarningDialog()
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "安全警告";
        DialogMessage.Text = "警告：使用“查找耳机”功能时，请勿将耳机戴在耳朵中。\n耳机响铃音量较大，戴在耳内可能造成永久性听力损伤。";
        DialogInput.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogMirrorBtn.IsVisible = false;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = "我已知晓，继续";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        return await _confirmTcs.Task;
    }
}

