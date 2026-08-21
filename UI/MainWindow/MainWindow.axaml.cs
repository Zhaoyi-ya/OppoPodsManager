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
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Subsystems.Desktop;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using NoiseOptionModel = OppoPodsManager.Control.Core.Features.NoiseOptionModel;
using MultiDeviceOperation = OppoPodsManager.Control.Core.Features.MultiDeviceOperation;
using MultiDeviceDisplayState = OppoPodsManager.Control.Core.Features.MultiDeviceDisplayState;
using OppoPodsManager.Control.Subsystems.Updates;
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
internal static class AppConst
{
    public const string WindowTitle = "OPPO Pods Manager";
    public const string IconConnected = "avares://OppoPodsManager/Assets/VisualAssets/tuopan.ico";
    public const string IconDisconnected = "avares://OppoPodsManager/Assets/VisualAssets/tuopandis.png";
}

// 从嵌入资源加载图标（适用于 Avalonia + AOT）

public partial class MainWindow : SukiWindow, IViewHost
{
    private FrontendState? _frontendState;
    private ControlManager? _controlManager;
    private CommandDispatcher? _commandDispatcher;
    private readonly SettingsStore _uiSettings;
    private IDisposable? _interactiveSurface;

    private ApplicationLog? _logManager;
    private DispatcherTimer? _bgApplyDebounceTimer;
    private IImage? _backgroundImageSource;
    private bool _realClose;
    private bool _runtimeUiDisposed;
    private bool _sukiWindowDisposed;
    private string _currentPage = "";
    // 统一管理主窗口和小窗使用的自定义背景资源。
    private readonly BackgroundImageManager _backgroundImages = new();
    private readonly BackgroundSelectionService _backgroundSelection;
    /// <summary>托盘 ANC 菜单项 → (发送键, 父键, 是否子模式)，避免闭包捕获。</summary>
    internal static ISukiToastManager ToastManager = new SukiToastManager();
    private string? _customDeviceName;

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
    // 复用控制层更新协调器，窗口只负责更新结果的显示和用户操作。
    private readonly UpdateCoordinator? _updateCoordinator;
    private readonly DesktopLinkService? _desktopLinks;
    private readonly FeedbackExportService? _feedbackExporter;
    private readonly Action? _requestApplicationExit;
    private readonly Func<bool>? _shouldKeepWindowAlive;

    // 三级联动数据由 SettingsView 持有；此处仅保留机型目录供注入。
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

        // 版本号统一取自 csproj 的 <Version>（经程序集元数据），提前写入 AppInfo，
        // 供关于页视图在 InitializeComponent 内读取，避免界面拆分后跨视图访问控件。
        var asmVer0 = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AppInfo.VersionLabel = asmVer0 != null
            ? $"v{asmVer0.Major}.{asmVer0.Minor}.{asmVer0.Build}"
            : "v?";

        InitializeComponent();

        AdaptToPlatform();
        AddHandler(PointerPressedEvent, CloseFloatingMenusOnBlankClick, RoutingStrategies.Tunnel, handledEventsToo: true);
        NavHome.Classes.Add("selected");

            _logManager?.Debug("UI", "InitializeComponent OK");

        // 初始化多语言字符串

        // 主题：默认跟随系统（CbTheme 选中项由 PersonalView 在 Attach 时设置）
        var themeIndex = NextThemeIndex();
        ApplyTheme(themeIndex);

        BackgroundAnimationEnabled = false;

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

        // 背景/自定义耳机图案/高级渲染/Acrylic 等个性化初始化由 PersonalView.Attach
        // 经 IViewHost 回调完成（外壳级副作用保持在外壳）。
        // 设备型号三级联动初始化由 SettingsView.Attach 完成（外壳仅保留窗口标题所需的纯数据）。

        // 自定义设备名由 PersonalView 在 Attach 时写入 TbCustomName；外壳仅保留用于窗口标题的纯数据。
        _customDeviceName = _uiSettings.GetString("CustomName");
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
        AttachViews();
        ApplyNextSnapshot(_frontendState.Snapshot);
        _ = HomeView?.RefreshNextDevicesAsync();
    }

    private void OnAvailableDevicesChanged(object? sender, DeviceOptionsChangedEventArgs args)
    {
        if (_realClose)
            return;

        Dispatcher.UIThread.Post(() => HomeView?.ApplyNextDevices(args.Devices));
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

        // 主页（状态/电量/ANC/功能/空间音频）经 HomeView 路由快照。
        UpdateTitle();
        HomeView?.ApplySnapshot(snapshot);
        GestureView?.ApplySnapshot(snapshot);
        SettingsView?.ApplySnapshot(snapshot);
        DeviceInfoView?.ApplySnapshot(snapshot);
        EqView?.ApplySnapshot(snapshot);
        SyncNextMultiDeviceList(snapshot);
        _logManager?.Debug("UI", $"快照已应用：revision={snapshot.Revision}，connected={snapshot.IsConnected}，device={snapshot.DeviceName ?? ""}。");
    }

    // ========== 拆分时遗留的字段/嵌套类型/辅助方法（原 MainWindow 顶部作用域）==========

    // ---- 浮层对话框 ----
    private TaskCompletionSource<string?>? _promptTcs;
    private TaskCompletionSource<bool>? _confirmTcs;
    private string _updatePendingVersion = ""; // 当前提示的新版本号，供跳过使用

    // ---- 耳机预览 ----
    private readonly Dictionary<EarphoneSlot, Image> _earphonePreviews = new();

    // ---- 多设备列表行引用 ----
    private readonly Dictionary<string, DeviceListRowRefs> _deviceListRows = new();

    private sealed class DeviceListRowRefs
    {
        public required Border Root { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock AudioText { get; init; }
        public required TextBlock StatusText { get; init; }
    }

    // ---- 视图注入与宿主接口实现 ----

    /// <summary>将共享服务注入各页面视图并绑定宿主回调，在控制层注入完成后调用。</summary>
    private void AttachViews()
    {
        AboutView.Host = this;
        AboutView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        LogView.Host = this;
        LogView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        EqView.Host = this;
        EqView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        PersonalView.Host = this;
        PersonalView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        SettingsView.ModelCatalog = _modelCatalog;
        SettingsView.Host = this;
        SettingsView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        DeviceInfoView.Host = this;
        DeviceInfoView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        HomeView.Host = this;
        HomeView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
        GestureView.Host = this;
        GestureView.Attach(_controlManager, _uiSettings, _logManager, _commandDispatcher, _frontendState, _desktopLinks);
    }

    void IViewHost.RequestNavigate(string page) => ShowPage(page);
    void IViewHost.OpenUrl(string url) => _desktopLinks?.TryOpen(url, "页面链接");
    Task IViewHost.ShowCheckResultDialogAsync(string message, string? title)
        => ShowCheckResultDialog(message, title);
    Task<string?> IViewHost.ShowPromptDialogAsync(string title, string defaultText, string hint)
        => ShowPromptDialog(title, defaultText, hint);
    Task<bool> IViewHost.ShowConfirmDialogAsync(string title, string message)
        => ShowConfirmDialog(title, message);

    // ---- 个性化页外壳级副作用 ----
    void IViewHost.ApplyTheme(int index)
    {
        ApplyTheme(index);
        _uiSettings.SetString("Theme", NextThemeName(index));
    }
    void IViewHost.ApplyLanguage(LanguageOption option) => ApplyLanguage(option);
    void IViewHost.RefreshCardOpacity() => RefreshCardOpacity();
    void IViewHost.SelectBackground(string key) => SelectBackground(key);
    void IViewHost.AddBackgroundImage() => _ = BtnBgAdd_Click();
    void IViewHost.ApplyBackgroundBlur()
    {
        var timer = EnsureBackgroundApplyDebounceTimer();
        timer.Stop();
        timer.Start();
    }
    void IViewHost.SetAcrylicBlur(bool on) => ToggleAcrylicBlur(on);
    void IViewHost.SetAcrylicBlurSilent(bool on) => ApplyAcrylicBlurSilently(on);
    void IViewHost.SetAdvancedRender(bool on)
    {
        if (on) EnableAdvancedRender();
        else DisableAdvancedRender();
    }
    void IViewHost.SetCustomDeviceName(string? name)
    {
        _customDeviceName = name;
        UpdateTitle();
    }
    void IViewHost.RebuildEarphoneUi() => BuildEarphoneCustomUi();
    void IViewHost.RefreshBackground()
    {
        RefreshBgThumbs();
        ApplySavedBackground();
    }

    // ---- 设置页外壳级能力 ----
    Task IViewHost.CheckForUpdatesAsync() => DoCheckUpdateAsync(silent: false);
    Task IViewHost.OpenFeedbackAsync() => ShowFeedbackDialogAsync();
    void IViewHost.ResyncMultiDeviceList() => SyncNextMultiDeviceList(_frontendState?.Snapshot);
    void IViewHost.SetEqControlsEnabled(bool enabled) => EqView?.SetControlsEnabled(enabled);
    Task<bool> IViewHost.ShowFindWarningDialogAsync() => ShowFindWarningDialog();

    private async Task ShowFeedbackDialogAsync()
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
}
