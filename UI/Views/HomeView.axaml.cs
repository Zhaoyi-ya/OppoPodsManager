using Avalonia.Controls.Shapes;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Brands.Oppo.Features;
using OppoPodsManager.UI.MainWindow;
using AvaloniaControl = Avalonia.Controls.Control;
using Path = Avalonia.Controls.Shapes.Path;
using OppoPodsManager.Control.Core.Features;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 主页视图：承载状态行、电量卡片、ANC 动态 UI、功能开关与空间音频。
/// 原 MainWindow.Device.cs / Anc.cs / Features.cs 中与此页控件相关的渲染与事件逻辑全部迁入此处；
/// 外壳 MainWindow 经 <see cref="ApplySnapshot"/> 路由快照，并经 IViewHost 提供查找耳机警告、EQ 控件启用等外壳级能力。
/// 共享画刷统一取自 <see cref="AppPalette"/>（与后续小窗复用）。
/// </summary>
public partial class HomeView : PageView
{
    // ---- 设备连接状态 ----
    private readonly List<DeviceConnectionOption> _nextDevices = new();
    private bool _suppressEarbudSelection;

    // ---- ANC 动态 UI（按 JSON 生成） ----
    private string _ancMain = "", _ancLevel = "";
    private readonly Dictionary<string, string> _ancLastSub = new();
    private readonly Dictionary<string, (Ellipse bg, Path icon, TextBlock label)> _ancMainButtons = new();
    private readonly Dictionary<string, (Button btn, Border bg)> _ancSubButtons = new();
    private readonly Dictionary<string, string> _ancChildToMain = new();
    private List<NoiseOptionModel> _ancOptions = new();
    private string _ancBuiltForModel = "";
    private string _ancSubSignature = "";

    // ---- 状态/功能本地状态 ----
    private DateTime _connectionStatusStartedAt = DateTime.MinValue;
    private bool _findDeviceActive;
    private bool _wasConnected;
    private bool _gameSoundCommandPending;
    private bool _realClose;

    public HomeView()
    {
        InitializeComponent();

        // 功能开关（原 MainWindow ctor 程序化接线，现由本视图自持）
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
        CbDevice.SelectionChanged += CbDevice_Changed;

        // 充电闪电图标（与设备信息页触控图同源，支持自定义图片覆盖）
        const string iconCharge = "M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z";
        LeftChargeBolt.Data = StreamGeometry.Parse(iconCharge);
        RightChargeBolt.Data = StreamGeometry.Parse(iconCharge);
        CaseChargeBolt.Data = StreamGeometry.Parse(iconCharge);

        ResetNextAncUi();
    }

    /// <summary>外壳在窗口关闭时调用，避免异步回追访问已释放控件。</summary>
    public void MarkClosed() => _realClose = true;

    // 电池图案由外壳经 EarphoneImageProvider 注入（与设备信息页触控图同源）。
    public Image BatteryLeftImage => LeftBatteryImage;
    public Image BatteryRightImage => RightBatteryImage;
    public Image BatteryCaseImage => CaseBatteryImage;

    public void SetAncPanelVisible(bool visible) => AncPanel.IsVisible = visible;

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        if (_realClose)
            return;

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

        ApplyNextFeatureState(snapshot);
        ApplyNextSpatialAudioSnapshot(snapshot);
        ApplyNextNoiseSnapshot(snapshot);
        BtnReconnect.IsVisible = !snapshot.IsConnected;
    }

    #region 连接状态与设备列表（原 MainWindow.Device.cs）

    private void UpdateNextConnectionStatus(BusinessSnapshot snapshot)
    {
        StatusDot.Fill = snapshot.IsConnected ? AppPalette.BrushGreen : AppPalette.BrushRed;
        StatusText.Foreground = snapshot.IsConnected ? AppPalette.BrushLightGreen : AppPalette.BrushLightRed;

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

        var manager = ControlManager?.ActiveManager;
        var modelName = manager?.Presentation.IsKnownModel == true
            ? manager.Presentation.ModelName
            : snapshot.Identity?.ModelName;
        if (!string.IsNullOrWhiteSpace(modelName)
            && !string.Equals(modelName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var connectedText = TranslationCatalog.Get("Status_Connected");
            StatusText.Text = string.Format(connectedText, modelName);
            return;
        }

        var identifyingText = TranslationCatalog.Get("Status_Identifying");
        var unidentifiedText = TranslationCatalog.Get("Status_Unidentified");
        StatusText.Text = DateTime.Now - _connectionStatusStartedAt < TimeSpan.FromSeconds(2)
            ? identifyingText
            : unidentifiedText;
    }

    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e)
    {
        Log?.Debug("UI", "用户操作: 刷新多耳机列表");
        if (ControlManager is null)
            return;

        // 注意：绝不能在此用 .GetResult()/.Result 同步阻塞 UI 线程——
        // 扫描链路(DiscoverAsync)跨线程执行，结束后续体需要回到 UI 线程才能跑完，
        // 而 GetResult() 会把 UI 线程占死，造成永久卡死(界面无响应)。改为 await 并在 UI 线程回写。
        var devices = await ControlManager.RefreshAvailableDevicesAsync(CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyNextDevices(devices));
    }

    internal void ApplyNextDevices(IReadOnlyList<DeviceConnectionOption> devices)
    {
        var selectedId = (CbDevice.SelectedItem as DeviceConnectionOption)?.Id
            ?? ControlManager?.ActiveDeviceId;
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
        if (selectedIndex < 0 && _nextDevices.Count > 0 && ControlManager?.ActiveManager is null)
            selectedIndex = 0;
        CbDevice.SelectedIndex = selectedIndex;
        _suppressEarbudSelection = false;
        Log?.Debug("UI", $"设备选择列表已更新：count={_nextDevices.Count}，selected={selectedId ?? ""}。");
    }

    private void CbDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEarbudSelection || ControlManager is null
            || CbDevice.SelectedIndex < 0 || CbDevice.SelectedIndex >= _nextDevices.Count)
            return;

        var selected = _nextDevices[CbDevice.SelectedIndex];
        Log?.Info("UI", $"用户选择设备：id={selected.Id}，name={selected.DisplayName}。");
        _ = ConnectNextDeviceAsync(selected.Id);
    }

    private void Reconnect_Click(object? s, RoutedEventArgs e)
    {
        Log?.Debug("UI", "用户操作: 点击重连");
        _ = ConnectNextDeviceAsync(null);
    }

    private async Task ConnectNextDeviceAsync(string? deviceId)
    {
        if (ControlManager is null)
            return;

        if (string.IsNullOrWhiteSpace(deviceId))
            await ControlManager.ConnectFirstAvailableAsync(CancellationToken.None);
        else
            await ControlManager.ConnectAsync(deviceId, CancellationToken.None);
    }

    internal async Task RefreshNextDevicesAsync()
    {
        if (ControlManager is null)
            return;

        var devices = await ControlManager.RefreshAvailableDevicesAsync(CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => ApplyNextDevices(devices));
    }

    #endregion

    #region ANC 动态 UI（原 MainWindow.Anc.cs）

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

    internal void RefreshAncLabels()
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
        var sep = new Border { Width = 1, Background = AppPalette.BrushGray, Opacity = 0.12 };
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

    internal void HighlightAnc()
    {
        var circleGray = AppPalette.CircleGray;
        var accent = AppPalette.Accent;
        foreach (var (key, (bg, icon, label)) in _ancMainButtons)
        {
            var active = key == _ancMain;
            bg.Fill = active ? accent : circleGray;
            icon.Fill = active ? AppPalette.BrushWhitePure : AppPalette.BrushGray;
        }
        foreach (var (key, (btn, bg)) in _ancSubButtons)
        {
            var active = key == _ancLevel;
            bg.Background = active ? accent : circleGray;
            btn.Foreground = active ? AppPalette.BrushWhitePure : AppPalette.BrushGray;
        }
    }

    private void SwitchAncMain(NoiseOptionModel opt)
    {
        Log?.Debug("UI", $"用户操作: ANC 主模式 -> {opt.Key}");
        if (ControlManager?.ActiveManager is null) return;
        _ancMain = opt.Key;

        if (opt.Children.Count > 0)
        {
            PopulateAncSub(opt);
            AncSubRow.IsVisible = true;
            var target = _ancLastSub.TryGetValue(opt.Key, out var last)
                && opt.Children.Any(c => c.Key == last)
                ? last : opt.Children[0].Key;
            _ancLevel = target;
            _ = CommandDispatcher?.RunAsync("ANC 子模式", manager => manager.SetNoiseCancellationByKeyAsync(target, CancellationToken.None));
        }
        else
        {
            AncSubRow.IsVisible = false;
            _ancLevel = "";
            _ = CommandDispatcher?.RunAsync("ANC 主模式", manager => manager.SetNoiseCancellationByKeyAsync(opt.Key, CancellationToken.None));
        }
        HighlightAnc();
    }

    private void SwitchAncSub(NoiseOptionModel opt)
    {
        Log?.Debug("UI", $"用户操作: ANC 子级别 -> {opt.Key}");
        if (ControlManager?.ActiveManager is null) return;
        _ancLevel = opt.Key;
        _ancLastSub[_ancMain] = opt.Key;
        _ = CommandDispatcher?.RunAsync("ANC 子模式", manager => manager.SetNoiseCancellationByKeyAsync(opt.Key, CancellationToken.None));
        HighlightAnc();
    }

    private void AncMain_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is NoiseOptionModel opt) SwitchAncMain(opt);
    }

    private void AncSub_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is NoiseOptionModel opt) SwitchAncSub(opt);
    }

    private void SyncAncFromState(string modeKey)
    {
        var mainOpt = _ancOptions.FirstOrDefault(o => o.Key == modeKey && o.Children.Count == 0);
        if (mainOpt != null)
        {
            _ancMain = modeKey;
            _ancLevel = "";
            AncSubRow.IsVisible = false;
            return;
        }

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

    private void ApplyNextNoiseSnapshot(BusinessSnapshot snapshot)
    {
        var manager = ControlManager?.ActiveManager;
        if (manager is null || !snapshot.IsConnected)
        {
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

    private async Task RunGameSoundCommandAsync(bool enabled)
    {
        _gameSoundCommandPending = true;
        try
        {
            await (CommandDispatcher?.RunAsync("游戏音效", manager => manager.SetGameSoundEnabledAsync(enabled, CancellationToken.None)) ?? Task.FromResult(false));
        }
        finally
        {
            _gameSoundCommandPending = false;
            if (!_realClose && FrontendState is not null)
                await Dispatcher.UIThread.InvokeAsync(() => ApplyNextFeatureState(FrontendState.Snapshot));
        }
    }

    internal void ResetNextAncUi()
    {
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

    private (AvaloniaControl panel, Ellipse bg, Ellipse stroke, Path icon, TextBlock label) MakeAncIconButton(
        NoiseOptionModel opt, int circleSize, int iconSize, int fontSize,
        EventHandler<RoutedEventArgs> onClick)
    {
        var bg = new Ellipse
        {
            Width = circleSize, Height = circleSize,
            Fill = AppPalette.BrushTransparent
        };
        var icon = new Path
        {
            Data = StreamGeometry.Parse(AncIcons.GetAncIcon(opt.Key)),
            Width = 24, Height = 24,
            Fill = AppPalette.BrushGray,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Tag = opt;
        var clickBtn = new Button
        {
            Width = circleSize, Height = circleSize,
            Background = AppPalette.BrushTransparent, BorderThickness = new Thickness(0),
            Tag = opt,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clickBtn.Click += onClick;

        var grid = new Grid { Width = circleSize, Height = circleSize };
        var hoverScale = new ScaleTransform(1, 1);
        grid.RenderTransform = hoverScale;
        grid.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
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
            Foreground = AppPalette.BrushGray,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(label);

        return (panel, bg, bg, icon, label);
    }

    private (Button, Border) MakeTextButton(string label, NoiseOptionModel opt, int w, int h, int fontSize, CornerRadius corner, EventHandler<RoutedEventArgs> onClick)
    {
        var btn = new Button
        {
            Content = label, Tag = opt, MinWidth = w, Height = h,
            BorderThickness = new Thickness(0), Padding = new Thickness(8, 0),
            Background = AppPalette.BrushTransparent, Focusable = false,
            Foreground = AppPalette.BrushGray, FontSize = fontSize,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btn.Click += onClick;
        var bg = new Border { CornerRadius = corner, Padding = new Thickness(0), Background = AppPalette.BrushTransparent, Child = btn };
        return (btn, bg);
    }

    #endregion

    #region 功能开关与空间音频（原 MainWindow.Features.cs）

    private void ApplyNextFeatureState(BusinessSnapshot snapshot)
    {
        var manager = ControlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        var hasFeatures = snapshot.IsConnected && presentation is not null;
        FeatureContentPanel.IsVisible = hasFeatures;
        FeaturePlaceholderText.IsVisible = !hasFeatures;
        if (!hasFeatures)
        {
            BtnFindDevice.IsVisible = false;
            SetFeatureControlsEnabled(false);
            _findDeviceActive = false;
            BtnFindDevice.Content = TranslationCatalog.Get("Feature_FindDevice");
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
        BtnFindDevice.IsEnabled = BtnFindDevice.IsVisible && snapshot.IsConnected;

        SetNextCheck(CbDualDevice, controlStates, "dual-device", CbDualDevice_Changed);
        SetNextCheck(CbBassEngine, controlStates, "bass-engine", CbBassEngine_Changed);
        SetNextCheck(CbVocalEnhance, controlStates, "voice-enhancement", CbVocalEnhance_Changed);
        SetNextCheck(CbHearingEnhance, controlStates, "hearing-enhancement", CbHearingEnhance_Changed);
        SetNextCheck(CbLongPower, controlStates, "long-battery", CbLongPower_Changed);
        SetNextCheck(CbWearDetection, controlStates, "wear-detection", CbWearDetection_Changed);
        SetNextCheck(CbSpineHealth, controlStates, "spine-health", CbSpineHealth_Changed);
        SetNextCheck(CbSpatial, controlStates, "spatial-sound", CbSpatial_Changed);
        if (!_gameSoundCommandPending && snapshot.Game.SoundType is { } gameSoundType)
            SetGameSoundCheckedSilent(gameSoundType != 0);
        if (controlStates.TryGetValue("game-mode", out var gameMode))
            SetGameCheckedSilent(gameMode);

        SetGameSoundEnabledSilent(GetControlEnabled(controlEnabledStates, "game-sound"));
        SetSpatialEnabledSilent(GetControlEnabled(controlEnabledStates, "spatial-sound"));
        Host?.SetEqControlsEnabled(GetControlEnabled(controlEnabledStates, "equalizer"));
    }

    private static bool GetControlEnabled(IReadOnlyDictionary<string, bool> states, string key)
        => !states.TryGetValue(key, out var enabled) || enabled;

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
        Host?.SetEqControlsEnabled(enabled);
    }

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
        Host?.SetEqControlsEnabled(GetControlEnabled(states, "equalizer"));
    }

    private void ApplyNextSpatialAudioSnapshot(BusinessSnapshot snapshot)
    {
        var manager = ControlManager?.ActiveManager;
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
            ? AppPalette.BrushBatteryLow
            : value.Percent <= 60 ? AppPalette.BrushBatteryMid : AppPalette.BrushBatteryHigh;
        progress.IsVisible = true;
    }

    private void SpatialAudio_Changed(object? s, RoutedEventArgs e)
    {
        if (s is RadioButton button && button.IsChecked == true && button.Tag is string mode)
            _ = CommandDispatcher?.RunAsync("空间音频", manager => manager.SetSpatialAudioByKeyAsync(mode, CancellationToken.None));
    }

    private void CbSpatial_Changed(object? s, RoutedEventArgs e)
    {
        if (CbSpatial.IsChecked is { } nextOn)
            _ = CommandDispatcher?.RunAsync("空间声场", manager => manager.SetSpatialSoundAsync(nextOn, CancellationToken.None));
    }

    private void CbGame_Changed(object? s, RoutedEventArgs e)
    {
        if (CbGame.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("游戏模式", manager => manager.SetGameModeAsync(enabled, CancellationToken.None));
    }

    private void CbGameSound_Changed(object? s, RoutedEventArgs e)
    {
        if (CbGameSound.IsChecked is { } enabled)
            _ = RunGameSoundCommandAsync(enabled);
    }

    private void SetGameSoundCheckedSilent(bool value)
    {
        if (CbGameSound.IsChecked == value)
            return;
        CbGameSound.IsCheckedChanged -= CbGameSound_Changed;
        CbGameSound.IsChecked = value;
        CbGameSound.IsCheckedChanged += CbGameSound_Changed;
    }

    private static void SetControlEnabledSilent(AvaloniaControl control, bool enabled)
    {
        if (control.IsEnabled != enabled)
            control.IsEnabled = enabled;
    }

    private void SetGameSoundEnabledSilent(bool enabled)
        => SetControlEnabledSilent(CbGameSound, enabled);

    private void SetSpatialEnabledSilent(bool enabled)
        => SetControlEnabledSilent(CbSpatial, enabled);

    private void SetGameCheckedSilent(bool value)
    {
        CbGame.IsCheckedChanged -= CbGame_Changed;
        CbGame.IsChecked = value;
        CbGame.IsCheckedChanged += CbGame_Changed;
    }

    private void CbDualDevice_Changed(object? s, RoutedEventArgs e)
    {
        if (CbDualDevice.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("双设备", manager => manager.SetDualDeviceAsync(enabled, CancellationToken.None));
    }

    private void CbBassEngine_Changed(object? s, RoutedEventArgs e)
    {
        if (CbBassEngine.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("低音引擎", manager => manager.SetBassEngineAsync(enabled, CancellationToken.None));
    }

    private void CbVocalEnhance_Changed(object? s, RoutedEventArgs e)
    {
        if (CbVocalEnhance.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("人声增强", manager => manager.SetVoiceEnhancementAsync(enabled, CancellationToken.None));
    }

    private void CbHearingEnhance_Changed(object? s, RoutedEventArgs e)
    {
        if (CbHearingEnhance.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("听力增强", manager => manager.SetHearingEnhancementAsync(enabled, CancellationToken.None));
    }

    private void CbLongPower_Changed(object? s, RoutedEventArgs e)
    {
        if (CbLongPower.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("长续航", manager => manager.SetLongBatteryAsync(enabled, CancellationToken.None));
    }

    private void CbWearDetection_Changed(object? s, RoutedEventArgs e)
    {
        if (CbWearDetection.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("佩戴检测", manager => manager.SetWearDetectionAsync(enabled, CancellationToken.None));
    }

    private void CbSpineHealth_Changed(object? s, RoutedEventArgs e)
    {
        if (CbSpineHealth.IsChecked is { } enabled)
            _ = CommandDispatcher?.RunAsync("脊柱健康", manager => manager.SetSpineHealthAsync(enabled, CancellationToken.None));
    }

    private async void BtnFindDevice_Click(object? s, RoutedEventArgs e)
    {
        if (ControlManager?.ActiveManager is null)
            return;

        if (!_findDeviceActive)
        {
            if (Host is null) return;
            var confirmed = await Host.ShowFindWarningDialogAsync();
            if (!confirmed)
                return;
        }

        _findDeviceActive = !_findDeviceActive;
        BtnFindDevice.Content = _findDeviceActive
            ? TranslationCatalog.Get("Feature_StopFindDevice")
            : TranslationCatalog.Get("Feature_FindDevice");
        _ = CommandDispatcher?.RunAsync("查找耳机", manager => manager.SetFindDeviceAsync(_findDeviceActive, CancellationToken.None));
    }

    internal void RefreshSpatialAudioLabels()
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

    #endregion
}
