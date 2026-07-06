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
using Avalonia.Threading;
using SukiUI.Controls;

namespace OppoPodsManager;

public partial class SmallWindow : SukiWindow
{
    private readonly IPodManager _pods;
    private readonly Action? _onDeactivated;

    private static readonly SolidColorBrush BrushGray   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x60, 0x90, 0xFF));
    private static readonly SolidColorBrush BrushWhite  = new(Colors.White);

    // 电量图标 path（从 OPPO 官方 App 提取，复合路径）
    private const string IconLeftData   = "M6,12C9.314,12 12,9.314 12,6C12,2.686 9.314,0 6,0C2.686,0 0,2.686 0,6C0,9.314 2.686,12 6,12Z";
    private const string IconLData       = "M3.963,9.543H8.604V8.337H5.458V2.461H3.963V9.543Z";
    private const string IconCaseData   = "M7.976,1.523H7.992H11.039H11.055H11.056C11.394,1.523 11.58,1.523 11.739,1.532C14.304,1.666 16.444,3.377 17.212,5.716H16.795C16.795,5.716 16.795,5.716 16.795,5.716H13.267C13.165,5.279 12.772,4.954 12.303,4.954H6.665C6.197,4.954 5.804,5.279 5.701,5.716H2.208C2.208,5.716 2.208,5.716 2.208,5.716H1.819C2.587,3.377 4.727,1.666 7.292,1.532C7.451,1.523 7.637,1.523 7.976,1.523H7.976Z M16.676,6.706H17.447C17.477,6.901 17.497,7.099 17.507,7.3C17.516,7.459 17.516,7.645 17.516,7.984V8V8.015C17.516,8.354 17.516,8.54 17.507,8.7C17.344,11.815 14.855,14.304 11.739,14.467C11.58,14.476 11.394,14.476 11.055,14.476H11.039H7.992H7.976C7.637,14.476 7.451,14.476 7.292,14.467C4.176,14.304 1.687,11.815 1.524,8.7C1.516,8.54 1.516,8.354 1.516,8.016V8.015V8V7.984V7.984C1.516,7.645 1.516,7.459 1.524,7.3C1.534,7.099 1.555,6.901 1.584,6.706H2.356C2.356,6.706 2.356,6.707 2.356,6.707H5.787C5.952,7.023 6.283,7.24 6.665,7.24H12.303C12.685,7.24 13.017,7.023 13.182,6.707H16.676C16.676,6.707 16.676,6.706 16.676,6.706Z M9.501,10.287C9.922,10.287 10.263,9.946 10.263,9.525C10.263,9.104 9.922,8.763 9.501,8.763C9.081,8.763 8.74,9.104 8.74,9.525C8.74,9.946 9.081,10.287 9.501,10.287Z";
    private const string IconRightData  = "M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14Z";
    private const string IconRData       = "M3.992,2.871V11.133H5.726V8.026H6.907L8.934,11.133H11.016L8.708,7.79C9.219,7.602 9.613,7.306 9.89,6.901C10.168,6.488 10.307,6.004 10.307,5.449C10.307,4.931 10.187,4.481 9.947,4.098C9.714,3.708 9.369,3.408 8.911,3.198C8.461,2.98 7.924,2.871 7.301,2.871H3.992Z M8.472,5.449C8.472,6.282 7.969,6.698 6.964,6.698H5.726V4.199H6.964C7.969,4.199 8.472,4.616 8.472,5.449Z";
    private const string IconChargeData = "M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z";


    private readonly Dictionary<string, (Ellipse bg, Path icon, TextBlock label)> _ancMainButtons = new();
    private readonly Dictionary<string, (Button btn, Border bg)> _ancSubButtons = new();
    private readonly Dictionary<string, string> _ancChildToMain = new();

    private List<AncOption> _ancOptions = new();
    private string _ancMain = "", _ancLevel = "";
    private string? _ancBuiltForModel;
    private DateTime _ancUserSetAt = DateTime.MinValue;

    public SmallWindow(IPodManager pods, Action? onDeactivated = null)
    {
        _pods = pods;
        _onDeactivated = onDeactivated;
        InitializeComponent();

        _pods.StateChanged += OnStateChanged;
        Deactivated += (_, _) =>
        {
            try { _onDeactivated?.Invoke(); }
            catch (Exception ex) { Log.D("UI", $"SmallWindow Deactivated 回调异常（可忽略）: {ex.Message}"); }
        };

        // 电池图标 path
        IconCase.Data  = StreamGeometry.Parse(IconCaseData);
        IconLeftCircle.Data  = StreamGeometry.Parse(IconLeftData);
        IconLeftLetter.Data  = StreamGeometry.Parse(IconLData);
        IconRightCircle.Data = StreamGeometry.Parse(IconRightData);
        IconRightLetter.Data = StreamGeometry.Parse(IconRData);
        IconCharge.Data = StreamGeometry.Parse(IconChargeData);

        // 加载官方充电盒产品图
        try
        {
            var bmp = AssetHelper.LoadBitmap("avares://OppoPodsManager/Assets/official_case.png");
            if (bmp != null) BatteryImage.Source = bmp;
        }
        catch { }

        SafeRefresh();
    }

    public void SafeRefresh()
        => Dispatcher.UIThread.Post(() => RefreshUi(_pods.State, _pods.Caps));

    private void OnStateChanged() => SafeRefresh();

    private void RefreshUi(PodState s, DeviceCapabilities caps)
    {
        // 标题栏显示设备名
        Title = s.Connected && caps.IsSupported ? caps.ModelName : "OPPO Pods";

        // 电量
        SetBatLabel(LeftLabel, s.Battery.GetValueOrDefault("L"));
        SetBatLabel(RightLabel, s.Battery.GetValueOrDefault("R"));
        SetBatLabel(CaseLabel, s.Battery.GetValueOrDefault("C"));
        // 充电指示
        var anyCharging = s.Battery.Values.Any(v => v?.Charging == true);
        ChargeIndicator.IsVisible = anyCharging;

        // ANC（使用通用 Syncer 映射设备回读的 modeKey → UI 选中态）
        if (s.AncMode is not "?" && (DateTime.Now - _ancUserSetAt).TotalMilliseconds > 3)
            SyncAncFromState(s.AncMode);
        AncPanel.IsVisible = caps.AncOptions.Count > 0 && s.Connected;
        if (AncPanel.IsVisible)
        {
            BuildAncUi(caps);
            HighlightAnc();
        }
    }

    // ===== 电量标签 =====
    private static void SetBatLabel(TextBlock label, (int Level, bool Charging)? bat)
    {
        if (bat is not { } b) { label.Text = "-%"; return; }
        label.Text = $"{b.Level}%";
    }

    // ===== ANC =====
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

    private void PopulateAncSub(AncOption container)
    {
        AncSubRow.Children.Clear();
        AncSubRow.ColumnDefinitions.Clear();
        _ancSubButtons.Clear();
        int col = 0;
        for (int i = 0; i < container.Children.Count; i++)
        {
            var child = container.Children[i];
            if (i > 0)
            {
                AncSubRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var sep = new Border { Width = 1, Background = BrushGray, Opacity = 0.12 };
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
                Content = child.Label, Tag = child, Width = 60, Height = 26,
                BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent), Focusable = false,
                Foreground = BrushGray, FontSize = 11
            };
            btn.Click += AncSub_Click;
            var bg = new Border { CornerRadius = corner, Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent), Child = btn };
            AncSubRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(bg, col);
            AncSubRow.Children.Add(bg);
            _ancSubButtons[child.Key] = (btn, bg);
            col++;
        }
    }

    private (Control panel, Ellipse bg, Path icon, TextBlock label) MakeAncIconButton(
        AncOption opt, int circleSize, int iconSize, int fontSize)
    {
        var bg = new Ellipse { Width = circleSize, Height = circleSize,
            Fill = new SolidColorBrush(Colors.Transparent) };
        var icon = new Path
        {
            Data = StreamGeometry.Parse(AncIcons.GetAncIcon(opt.Key)),
            Width = iconSize, Height = iconSize, Fill = BrushGray,
            Stretch = Stretch.Uniform
        };
        var clickArea = new Ellipse
        {
            Width = circleSize, Height = circleSize,
            Fill = new SolidColorBrush(Colors.Transparent),
            Tag = opt, Cursor = new Cursor(StandardCursorType.Hand)
        };
        clickArea.PointerPressed += (s, _) =>
        {
            if (s is Ellipse el && el.Tag is AncOption o) SwitchAncMain(o);
        };

        var grid = new Grid { Width = circleSize, Height = circleSize };
        grid.Children.Add(bg);
        grid.Children.Add(icon);
        grid.Children.Add(clickArea);

        var label = new TextBlock
        {
            Text = opt.Label, FontSize = fontSize, Foreground = BrushGray,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
        };

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(label);
        return (panel, bg, icon, label);
    }

    private void AncSub_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is AncOption opt) SwitchAncSub(opt);
    }

    private void HighlightAnc()
    {
        var inactiveBg = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        var subInactiveBg = new SolidColorBrush(Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        foreach (var (key, (bg, icon, label)) in _ancMainButtons)
        {
            var active = key == _ancMain;
            bg.Fill   = active ? BrushAccent : inactiveBg;
            icon.Fill = active ? BrushWhite : BrushGray;
        }
        foreach (var (key, (btn, bg)) in _ancSubButtons)
        {
            var active = key == _ancLevel;
            bg.Background = active ? BrushAccent : subInactiveBg;
            btn.Foreground = active ? BrushWhite : BrushGray;
        }
    }

    private void SwitchAncMain(AncOption opt)
    {
        if (!_pods.IsConnected) return;
        _ancUserSetAt = DateTime.Now;
        _ancMain = opt.Key;

        if (opt.Children.Count > 0)
        {
            PopulateAncSub(opt);
            AncSubRow.IsVisible = true;
            var target = opt.Children.Any(c => c.Key == _ancLevel) ? _ancLevel : opt.Children[0].Key;
            _ancLevel = target;
            _pods.SendAnc(target);   // 容器型发子键，非父键
        }
        else
        {
            AncSubRow.IsVisible = false;
            _ancLevel = "";
            _pods.SendAnc(opt.Key);  // 叶子型直接发
        }
        HighlightAnc();
    }

    private void SwitchAncSub(AncOption opt)
    {
        if (!_pods.IsConnected) return;
        _ancLevel = opt.Key;
        _ancUserSetAt = DateTime.Now;
        _pods.SendAnc(opt.Key);
        HighlightAnc();
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
