using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SukiUI;
using SukiUI.Controls;
using AvaloniaControl = Avalonia.Controls.Control;
using PathShape = Avalonia.Controls.Shapes.Path;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Oppo.Features;
using EarphoneImageProvider = OppoPodsManager.Assets.VisualAssets.EarphoneImageProvider;
using EarphoneSlot = OppoPodsManager.Assets.VisualAssets.EarphoneSlot;
using AncIcons = OppoPodsManager.UI.MainWindow.AncIcons;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Logging;
using BackgroundImageManager = OppoPodsManager.Assets.VisualAssets.BackgroundImageManager;
using DeviceProfileLoader = OppoPodsManager.Assets.Localization.DeviceProfileLoader;
using AssetHelper = OppoPodsManager.Assets.VisualAssets.AssetHelper;

namespace OppoPodsManager.UI.MiniWindow.Status;

public partial class SmallWindow : SukiWindow
{
    private readonly Action? _onDeactivated;
    private FrontendState? _frontendState;
    private ControlManager? _controlManager;
    private CommandDispatcher? _commandDispatcher;
    private IBrandManager? _nextManager;
    private OppoPodsManager.Assets.UserSettings.SettingsManager? _nextSettings;
    private IDisposable? _interactiveSurface;

    // 窗口外观相关画刷（卡片背景/边框/窗口/背景染色）保留在小窗，由 _nextSettings 驱动。
    // ANC/状态等语义色彩统一取自共享 AppPalette（与主窗口一致，随主题切换）。
    private readonly SolidColorBrush _cardBgBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _cardBorderBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _windowBgBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _bgTintBrush = new(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private Avalonia.Media.Imaging.Bitmap? _backgroundBitmap;
    private string _backgroundCacheKey = "";

    // 为 Avalonia/AOT 资源加载器提供不触发业务逻辑的公开构造入口。
    public SmallWindow()
    {
        InitializeComponent();
    }

    // 电量图标 path（从 OPPO 官方 App 提取，复合路径）
    private const string IconLeftData   = "M6,12C9.314,12 12,9.314 12,6C12,2.686 9.314,0 6,0C2.686,0 0,2.686 0,6C0,9.314 2.686,12 6,12Z";
    private const string IconLData       = "M3.963,9.543H8.604V8.337H5.458V2.461H3.963V9.543Z";
    private const string IconCaseData   = "M7.976,1.523H7.992H11.039H11.055H11.056C11.394,1.523 11.58,1.523 11.739,1.532C14.304,1.666 16.444,3.377 17.212,5.716H16.795C16.795,5.716 16.795,5.716 16.795,5.716H13.267C13.165,5.279 12.772,4.954 12.303,4.954H6.665C6.197,4.954 5.804,5.279 5.701,5.716H2.208C2.208,5.716 2.208,5.716 2.208,5.716H1.819C2.587,3.377 4.727,1.666 7.292,1.532C7.451,1.523 7.637,1.523 7.976,1.523H7.976Z M16.676,6.706H17.447C17.477,6.901 17.497,7.099 17.507,7.3C17.516,7.459 17.516,7.645 17.516,7.984V8V8.015C17.516,8.354 17.516,8.54 17.507,8.7C17.344,11.815 14.855,14.304 11.739,14.467C11.58,14.476 11.394,14.476 11.055,14.476H11.039H7.992H7.976C7.637,14.476 7.451,14.476 7.292,14.467C4.176,14.304 1.687,11.815 1.524,8.7C1.516,8.54 1.516,8.354 1.516,8.016V8.015V8V7.984V7.984C1.516,7.645 1.516,7.459 1.524,7.3C1.534,7.099 1.555,6.901 1.584,6.706H2.356C2.356,6.706 2.356,6.707 2.356,6.707H5.787C5.952,7.023 6.283,7.24 6.665,7.24H12.303C12.685,7.24 13.017,7.023 13.182,6.707H16.676C16.676,6.707 16.676,6.706 16.676,6.706Z M9.501,10.287C9.922,10.287 10.263,9.946 10.263,9.525C10.263,9.104 9.922,8.763 9.501,8.763C9.081,8.763 8.74,9.104 8.74,9.525C8.74,9.946 9.081,10.287 9.501,10.287Z";
    private const string IconRightData  = "M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14Z";
    private const string IconRData       = "M3.992,2.871V11.133H5.726V8.026H6.907L8.934,11.133H11.016L8.708,7.79C9.219,7.602 9.613,7.306 9.89,6.901C10.168,6.488 10.307,6.004 10.307,5.449C10.307,4.931 10.187,4.481 9.947,4.098C9.714,3.708 9.369,3.408 8.911,3.198C8.461,2.98 7.924,2.871 7.301,2.871H3.992Z M8.472,5.449C8.472,6.282 7.969,6.698 6.964,6.698H5.726V4.199H6.964C7.969,4.199 8.472,4.616 8.472,5.449Z";
    private const string IconChargeData = "M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z";


    private readonly Dictionary<string, (Ellipse bg, PathShape icon, TextBlock label)> _ancMainButtons = new();
    private readonly Dictionary<string, (Button btn, Border bg)> _ancSubButtons = new();
    private readonly Dictionary<string, string> _ancChildToMain = new();

    private List<NoiseOptionModel> _ancOptions = new();
    private string _ancMain = "", _ancLevel = "";
    private string? _ancBuiltForModel;
    private string _ancSubSignature = "";
    private bool _refreshPending;
    private bool _isClosed;
    private bool _sukiWindowDisposed;
    private DateTime _ancUserSetAt = DateTime.MinValue;

    // 初始化原项目小窗口的控件、事件和视觉资源。
    private void InitializeWindow()
    {
        InitializeComponent();

        ApplicationLog.Current?.Debug("UI", "SmallWindow: 打开");
        // 窗口关闭时取消订阅，避免对已关闭窗口的控件操作 + 释放引用
        Closed += (_, _) =>
        {
            ApplicationLog.Current?.Debug("UI", "SmallWindow: 关闭");
            _isClosed = true;
            if (_frontendState is not null)
                _frontendState.Changed -= OnNextStateChanged;
            PropertyChanged -= OnWindowPropertyChanged;
            _interactiveSurface?.Dispose();
            _interactiveSurface = null;
            DisposeWindowImages();
            DisposeBackgroundBitmap();
            DisposeSukiWindow();
        };
        Deactivated += (_, _) =>
        {
            try { _onDeactivated?.Invoke(); }
            catch (Exception ex) { ApplicationLog.Current?.Debug("UI", $"SmallWindow Deactivated 回调异常（可忽略）: {ex.Message}"); }
        };

        // 电池图标 path
        IconCase.Data  = StreamGeometry.Parse(IconCaseData);
        IconLeftCircle.Data  = StreamGeometry.Parse(IconLeftData);
        IconLeftLetter.Data  = StreamGeometry.Parse(IconLData);
        IconRightCircle.Data = StreamGeometry.Parse(IconRightData);
        IconRightLetter.Data = StreamGeometry.Parse(IconRData);
        var chargeGeo = StreamGeometry.Parse(IconChargeData);
        LeftChargeBolt.Data = chargeGeo;
        RightChargeBolt.Data = chargeGeo;
        CaseChargeBolt.Data = chargeGeo;

        // 加载耳机图案：双耳机 + 充电盒（支持配置目录自定义图片覆盖）
        LoadEarphoneImages();

        RefreshAppearance();
        SafeRefresh();
    }

    // 接收 Next 控制层快照，复用原项目小窗布局而不触碰通信对象。
    public SmallWindow(
        FrontendState frontendState,
        ControlManager controlManager,
        CommandDispatcher commandDispatcher,
        OppoPodsManager.Assets.UserSettings.SettingsManager? settings = null,
        Action? onDeactivated = null)
    {
        _onDeactivated = onDeactivated;
        _controlManager = controlManager;
        // 小窗口只接收应用层命令调度器，不在窗口内构造控制逻辑。
        _commandDispatcher = commandDispatcher;
        _nextSettings = settings;
        InitializeWindow();
        _frontendState = frontendState;
        _nextManager = controlManager.ActiveManager;
        _frontendState.Changed += OnNextStateChanged;
        PropertyChanged += OnWindowPropertyChanged;
        ApplyNextSnapshot(_frontendState.Snapshot);
    }

    // 小窗显示时临时持有交互轮询租约，隐藏或关闭后立即释放。
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

    // 把 Next 状态切换到 Avalonia UI 线程，避免通知线程直接访问控件。
    private void OnNextStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyNextSnapshot(snapshot));
    }

    // 更新小窗的电量和降噪卡片可见性，具体协议状态由控制层提供。
    private void ApplyNextSnapshot(BusinessSnapshot snapshot)
    {
        if (_isClosed)
            return;

        SetNextBattery(LeftLabel, LeftChargeBolt, snapshot.LeftBattery);
        SetNextBattery(RightLabel, RightChargeBolt, snapshot.RightBattery);
        SetNextBattery(CaseLabel, CaseChargeBolt, snapshot.CaseBattery);
        Title = DeviceText.DeviceName(snapshot.Identity?.ModelName, snapshot.DeviceName);
        var manager = _controlManager?.ActiveManager ?? _nextManager;
        if (manager is not null)
        {
            var presentation = manager.Presentation;
            AncCard.IsVisible = snapshot.IsConnected && presentation.SupportsNoiseCancellation;
            if (AncCard.IsVisible)
            {
                BuildNextAncUi(presentation);
                if (snapshot.Noise.Mode != NoiseMode.Unknown)
                    SyncAncFromState(presentation.CurrentNoiseModeKey);
                HighlightAnc();
            }
        }
        else
        {
            AncCard.IsVisible = false;
        }
        ApplicationLog.Current?.Debug("UI", $"小窗已应用快照：revision={snapshot.Revision}，connected={snapshot.IsConnected}。");
    }

    // 显示 Next 快照中的电量和充电状态。
    private static void SetNextBattery(TextBlock label, AvaloniaControl bolt, BatteryLevel? battery)
    {
        label.Text = battery is { } value ? $"{value.Percent}%" : "-%";
        bolt.IsVisible = battery?.IsCharging == true;
    }

    public void RefreshAppearance()
    {
        if (_isClosed)
            return;
        ApplyAcrylicBlur();
        ApplyWindowChrome();
        ApplyTheme();
    }

    // 统一读取两套运行模式的窗口设置，避免 Next 小窗访问旧项目的静态设置。
    private bool ReadBoolSetting(string key, bool fallback)
    {
        if (_nextSettings is null)
            return fallback;

        return key switch
        {
            "AcrylicBlur" => _nextSettings.Current.AcrylicBlur,
            "AdvancedRender" => _nextSettings.Current.AdvancedRender,
            _ => fallback
        };
    }

    // 统一读取两套运行模式的整数窗口设置。
    private int ReadIntSetting(string key, int fallback)
    {
        if (_nextSettings is null)
            return fallback;

        return key switch
        {
            "CardOpacity" => _nextSettings.Current.CardOpacity,
            "BgBlur" => _nextSettings.Current.BackgroundBlur,
            _ => fallback
        };
    }

    // 统一读取两套运行模式的背景文件设置。
    private string? ReadStringSetting(string key)
    {
        if (_nextSettings is null)
            return null;

        return key == "BgCurrent" ? _nextSettings.Current.BackgroundPath : null;
    }

    private void LoadEarphoneImages()
    {
        ReplaceEarphoneImage(SmallDualImage, EarphoneSlot.SmallDual);
        ReplaceEarphoneImage(SmallCaseImage, EarphoneSlot.Case);
    }

    // 替换图片前释放旧的独立位图，避免小窗反复刷新积累本地资源。
    private static void ReplaceEarphoneImage(Image image, EarphoneSlot slot)
    {
        try
        {
            var old = image.Source;
            image.Source = EarphoneImageProvider.GetBitmap(slot);
            if (old is Bitmap oldBitmap && !AssetHelper.IsShared(oldBitmap))
                oldBitmap.Dispose();
        }
        catch
        {
        }
    }

    // 关闭小窗时释放其独立的耳机和充电盒位图。
    private void DisposeWindowImages()
    {
        DisposeEarphoneImage(SmallDualImage);
        DisposeEarphoneImage(SmallCaseImage);
    }

    // 调用 SukiWindow 自带的释放流程，清理小窗的 Toast host 和渲染资源。
    private void DisposeSukiWindow()
    {
        if (_sukiWindowDisposed)
            return;

        _sukiWindowDisposed = true;
        Dispose();
    }

    // 释放小窗单独创建的耳机位图。
    private static void DisposeEarphoneImage(Image image)
    {
        if (image.Source is Bitmap bitmap && !AssetHelper.IsShared(bitmap))
            bitmap.Dispose();
        image.Source = null;
    }

    /// <summary>自定义耳机图案变化后，重新加载小 UI 的双耳机与充电盒图。</summary>
    public void RefreshEarphoneImages() => LoadEarphoneImages();

    private void ApplyAcrylicBlur()
    {
        if (!ReadBoolSetting("AcrylicBlur", false))
            return;

        TransparencyLevelHint = new List<WindowTransparencyLevel>
        {
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent
        };
        Background = Avalonia.Media.Brushes.Transparent;
        if (OperatingSystem.IsWindows())
            BackgroundShaderCode = "vec4 main(vec2 fragCoord) { return vec4(0.0); }";
        ApplicationLog.Current?.Debug("UI", "SmallWindow: 应用 Acrylic 模糊");
    }

    private void ApplyWindowChrome()
    {
        if (ReadBoolSetting("AdvancedRender", false))
            EnableAdvancedRenderChrome();
        else
            DisableAdvancedRenderChrome();
    }

    private void EnableAdvancedRenderChrome()
    {
        IsTitleBarVisible = false;
        CustomTitleBar.IsVisible = true;
        ApplicationLog.Current?.Debug("UI", "SmallWindow: 关闭 Chrome 标题栏 -> true");
    }

    private void DisableAdvancedRenderChrome()
    {
        IsTitleBarVisible = true;
        CustomTitleBar.IsVisible = false;
        ApplicationLog.Current?.Debug("UI", "SmallWindow: 关闭 Chrome 标题栏 -> false");
    }

    private void TitleBarDrag_PointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e)
        => BeginMoveDrag(e);

    private void CustomClose_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplicationLog.Current?.Debug("UI", "SmallWindow: 点击自定义关闭按钮");
        Close();
    }

    private void ApplyTheme()
    {
        var theme = SukiTheme.GetInstance();
        var activeTheme = theme.ActiveBaseTheme == Avalonia.Styling.ThemeVariant.Default
            ? Application.Current?.ActualThemeVariant
            : theme.ActiveBaseTheme;
        var isLight = activeTheme == Avalonia.Styling.ThemeVariant.Light;
        // 同步共享调色板主题标志，小窗 ANC/状态取色与主窗口保持一致。
        AppPalette.IsLightTheme = isLight;
        var transparencyPct = Math.Clamp(ReadIntSetting("CardOpacity", 50), 0, 90);
        var alpha = (byte)Math.Clamp(255 - (transparencyPct * 255 / 100), 25, 255);
        var hasCustomBackground = HasCustomBackgroundEnabled();

        var acrylicBlur = ReadBoolSetting("AcrylicBlur", false);
        if (acrylicBlur)
        {
            Background = Avalonia.Media.Brushes.Transparent;
        }
        else
        {
            _windowBgBrush.Color = isLight ? Color.FromRgb(0xE5, 0xE5, 0xEA) : Colors.Transparent;
            Background = _windowBgBrush;
        }

        if (isLight)
        {
            var cardBase = hasCustomBackground ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0xF5, 0xF5, 0xF5);
            _cardBgBrush.Color = Color.FromArgb(alpha, cardBase.R, cardBase.G, cardBase.B);
            _cardBorderBrush.Color = Color.FromArgb(0x15, 0x00, 0x00, 0x00);
            _bgTintBrush.Color = Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF);
            BatteryCard.BorderBrush = _cardBorderBrush;
            AncCard.BorderBrush = _cardBorderBrush;
        }
        else
        {
            // 与大 UI 保持一致：暗色卡片直接使用同一 alpha，不再乘 0.35，避免两窗口严重不同步
            _cardBgBrush.Color = Color.FromArgb(alpha, 0x1C, 0x1C, 0x1E);
            _bgTintBrush.Color = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            BatteryCard.BorderBrush = null;
            AncCard.BorderBrush = null;
        }

        BatteryCard.Background = _cardBgBrush;
        AncCard.Background = _cardBgBrush;
        BgTint.Background = _bgTintBrush;
        ApplySavedBackground();
    }

    private bool HasCustomBackgroundEnabled()
    {
        var key = ReadStringSetting("BgCurrent");
        return !ReadBoolSetting("AcrylicBlur", false)
               && !string.IsNullOrWhiteSpace(key)
               && key != "default"
               && System.IO.File.Exists(key);
    }

    private void ApplySavedBackground()
    {
        var key = ReadStringSetting("BgCurrent");
        if (ReadBoolSetting("AcrylicBlur", false) || string.IsNullOrWhiteSpace(key) || key == "default" || !System.IO.File.Exists(key))
        {
            ApplicationLog.Current?.Debug("UI", "SmallWindow: 自定义背景未启用或被 Acrylic 禁用");
            BgImage.Source = null;
            BgImage.IsVisible = false;
            BgTint.IsVisible = false;
            DisposeBackgroundBitmap();
            return;
        }

        var blur = Math.Clamp(ReadIntSetting("BgBlur", 0), 0, 20);
        var cacheKey = key + "|" + blur;
        if (_backgroundCacheKey != cacheKey)
        {
            DisposeBackgroundBitmap();
            // 复用资源层的裁剪和模糊算法，保证小窗背景与主窗口显示一致。
            _backgroundBitmap = BackgroundImageManager.LoadShared(key, 420, blur);
            _backgroundCacheKey = cacheKey;
        }

        BgImage.Source = _backgroundBitmap;
        BgImage.IsVisible = _backgroundBitmap != null;
        BgTint.IsVisible = _backgroundBitmap != null;
    }

    private void DisposeBackgroundBitmap()
    {
        BgImage.Source = null;
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        _backgroundCacheKey = "";
    }

    public void SafeRefresh()
    {
        if (_isClosed || _refreshPending)
            return;

        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (_isClosed)
                return;

            if (_frontendState is not null)
                ApplyNextSnapshot(_frontendState.Snapshot);
        });
    }

    // ===== ANC =====
    private void BuildAncUi(string modelKey, IReadOnlyList<NoiseOptionModel> options)
    {
        if (_ancBuiltForModel == modelKey) return;
        _ancBuiltForModel = modelKey;
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

        int col = 0;
        for (int i = 0; i < _ancOptions.Count; i++)
        {
            var opt = _ancOptions[i];
            if (i > 0) { AncMainRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10))); col++; }
            var (panel, bg, icon, label) = MakeAncIconButton(opt, 46, 22, 10);
            AncMainRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(panel, col);
            AncMainRow.Children.Add(panel);
            _ancMainButtons[opt.Key] = (bg, icon, label);
            col++;
            foreach (var child in opt.Children) _ancChildToMain[child.Key] = opt.Key;
        }
    }

    // 将后端降噪模式模型转换为原小窗控件使用的层级模型。
    private void BuildNextAncUi(BrandPresentation presentation)
    {
        var modelKey = presentation.ModelName + "|"
            + string.Join("|", presentation.NoiseOptions.Select(option =>
                $"{option.Key}:{string.Join(',', option.Children.Select(child => child.Key))}"));
        if (_ancBuiltForModel == modelKey)
            return;

        BuildAncUi(modelKey, presentation.NoiseOptions);
    }

    private void PopulateAncSub(NoiseOptionModel container)
    {
        var signature = container.Key + ":" + string.Join("|", container.Children.Select(c => $"{c.Key};{DeviceProfileLoader.AncLabel(c.Key)}"));
        if (_ancSubSignature == signature)
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
            if (i > 0)
            {
                AncSubRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var sep = new Border { Width = 1, Background = AppPalette.BrushGray, Opacity = 0.12 };
                Grid.SetColumn(sep, col);
                AncSubRow.Children.Add(sep);
                col++;
            }
            var corner = container.Children.Count == 1 ? new CornerRadius(5)
                : i == 0 ? new CornerRadius(5, 0, 0, 5)
                : i == container.Children.Count - 1 ? new CornerRadius(0, 5, 5, 0)
                : new CornerRadius(0);
            var btn = new Button
            {
                Content = DeviceProfileLoader.AncLabel(child.Key), Tag = child, MinWidth = 60, Height = 26,
                BorderThickness = new Thickness(0), Padding = new Thickness(8, 0),
                Background = Brushes.Transparent, Focusable = false,
                Foreground = AppPalette.BrushGray, FontSize = 13,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            btn.Click += AncSub_Click;
            var bg = new Border { CornerRadius = corner, Padding = new Thickness(0),
                Background = Brushes.Transparent, Child = btn };
            AncSubRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(bg, col);
            AncSubRow.Children.Add(bg);
            _ancSubButtons[child.Key] = (btn, bg);
            col++;
        }
    }

    /// <summary>切换语言后，用实时本地化标签刷新已生成的 ANC 主/子按钮文字（由 MainWindow 在语言切换时调用）。</summary>
    internal void RefreshAncLabels()
    {
        foreach (var (key, (_, _, label)) in _ancMainButtons)
        {
            var t = DeviceProfileLoader.AncLabel(key);
            label.Text = t;
            label.FontSize = t.Length > 10 ? 8 : 10;
        }
        foreach (var (key, (btn, _)) in _ancSubButtons)
            btn.Content = DeviceProfileLoader.AncLabel(key);
    }

    private (AvaloniaControl panel, Ellipse bg, PathShape icon, TextBlock label) MakeAncIconButton(
        NoiseOptionModel opt, int circleSize, int iconSize, int fontSize)
    {
        var bg = new Ellipse { Width = circleSize, Height = circleSize,
            Fill = Brushes.Transparent };
        var icon = new PathShape
        {
            Data = StreamGeometry.Parse(AncIcons.GetAncIcon(opt.Key)),
            Width = iconSize, Height = iconSize, Fill = AppPalette.BrushGray,
            Stretch = Stretch.Uniform
        };
        var clickArea = new Ellipse
        {
            Width = circleSize, Height = circleSize,
            Fill = Brushes.Transparent,
            Tag = opt, Cursor = new Cursor(StandardCursorType.Hand)
        };
        clickArea.PointerPressed += (s, _) =>
        {
            if (s is Ellipse el && el.Tag is NoiseOptionModel o) SwitchAncMain(o);
        };

        var grid = new Grid { Width = circleSize, Height = circleSize };
        grid.Children.Add(bg);
        grid.Children.Add(icon);
        grid.Children.Add(clickArea);

        var labelText = DeviceProfileLoader.AncLabel(opt.Key);
        var label = new TextBlock
        {
            Text = labelText, FontSize = labelText.Length > 10 ? Math.Max(8, fontSize - 2) : fontSize,
            Foreground = AppPalette.BrushGray, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(label);
        return (panel, bg, icon, label);
    }

    private void AncSub_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is NoiseOptionModel opt) SwitchAncSub(opt);
    }

    private void HighlightAnc()
    {
        foreach (var (key, (bg, icon, label)) in _ancMainButtons)
        {
            var active = key == _ancMain;
            bg.Fill   = active ? AppPalette.Accent : AppPalette.CircleGray;
            icon.Fill = active ? AppPalette.BrushWhitePure : AppPalette.BrushGray;
        }
        foreach (var (key, (btn, bg)) in _ancSubButtons)
        {
            var active = key == _ancLevel;
            bg.Background = active ? AppPalette.Accent : AppPalette.BrushCircleStrokeInactive;
            btn.Foreground = active ? AppPalette.BrushWhitePure : AppPalette.BrushGray;
        }
    }

    private void SwitchAncMain(NoiseOptionModel opt)
    {
        if (_frontendState?.Snapshot.IsConnected != true) return;
        _ancUserSetAt = DateTime.Now;
        _ancMain = opt.Key;

        if (opt.Children.Count > 0)
        {
            PopulateAncSub(opt);
            AncSubRow.IsVisible = true;
            var target = opt.Children.Any(c => c.Key == _ancLevel) ? _ancLevel : opt.Children[0].Key;
            _ancLevel = target;
            ApplicationLog.Current?.Debug("UI", $"SmallWindow: ANC 主模式 -> {opt.Key}, 发送子模式 {target}");
            _ = SetNextAncAsync(target, "ANC 子模式");
        }
        else
        {
            AncSubRow.IsVisible = false;
            _ancLevel = "";
            ApplicationLog.Current?.Debug("UI", $"SmallWindow: ANC 主模式 -> {opt.Key}");
            _ = SetNextAncAsync(opt.Key, "ANC 主模式");
        }
        HighlightAnc();
    }

    private void SwitchAncSub(NoiseOptionModel opt)
    {
        if (_frontendState?.Snapshot.IsConnected != true) return;
        _ancLevel = opt.Key;
        _ancUserSetAt = DateTime.Now;
        ApplicationLog.Current?.Debug("UI", $"SmallWindow: ANC 子模式 -> {opt.Key}");
        _ = SetNextAncAsync(opt.Key, "ANC 子模式");
        HighlightAnc();
    }

    // 通过控制层发送模式键并记录结果，避免小窗直接处理协议索引。
    private async Task SetNextAncAsync(string modeKey, string operation)
    {
        if (_commandDispatcher is null)
            return;

        await _commandDispatcher.RunAsync(
            operation,
            manager => manager.SetNoiseCancellationByKeyAsync(modeKey, CancellationToken.None));
    }

    /// <summary>把设备上报的 ANC 模式键映射到 UI 主/子选中态（与 MainWindow 逻辑一致）。</summary>
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
}
