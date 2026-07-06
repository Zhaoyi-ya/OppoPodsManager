using System.Threading.Tasks;
using Avalonia.Media.Transformation;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Threading;
using SukiUI;

namespace OppoPodsManager;

public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
        // 闪电图标向量（替代 ⚡ 避免 MiSans 缺失显示为方框）
        var boltGeo = StreamGeometry.Parse("M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z");
        LeftBolt.Data = boltGeo;
        CaseBolt.Data = boltGeo;
        RightBolt.Data = boltGeo;
        LowBolt.Data = boltGeo;
        CritBolt.Data = boltGeo;
    }

    /// <summary>桌面右下角半透明 Toast 显示，指定毫秒后自动关闭</summary>
    public static async Task ShowAsync(PodState state, string deviceName, int durationMs = 5000)
    {
        var toast = new ToastWindow();
        toast.TitleBlock.Text = deviceName;
        SetBat(toast.LeftPct, toast.LeftBolt, TryGet(state.Battery, "L"));
        SetBat(toast.RightPct, toast.RightBolt, TryGet(state.Battery, "R"));
        SetBat(toast.CasePct, toast.CaseBolt, TryGet(state.Battery, "C"));
        await ShowAndClose(toast, durationMs);
    }

    public static async Task ShowLowBatteryAsync(PodState state, string deviceName)
    {
        var toast = new ToastWindow();
        toast.TitleBlock.Text = deviceName;
        SetBat(toast.LeftPct, toast.LeftBolt, TryGet(state.Battery, "L"));
        SetBat(toast.RightPct, toast.RightBolt, TryGet(state.Battery, "R"));
        SetBat(toast.CasePct, toast.CaseBolt, TryGet(state.Battery, "C"));
        toast.LowBatteryOverlay.IsVisible = true;
        await ShowAndClose(toast, 5000);
    }

    public static async Task ShowCriticalBatteryAsync(PodState state, string deviceName)
    {
        var toast = new ToastWindow();
        toast.TitleBlock.Text = deviceName;
        SetBat(toast.LeftPct, toast.LeftBolt, TryGet(state.Battery, "L"));
        SetBat(toast.RightPct, toast.RightBolt, TryGet(state.Battery, "R"));
        SetBat(toast.CasePct, toast.CaseBolt, TryGet(state.Battery, "C"));
        toast.CriticalBatteryOverlay.IsVisible = true;
        await ShowAndClose(toast, 5000);
    }

    public static async Task ShowDisconnectedAsync(string deviceName)
    {
        var toast = new ToastWindow();
        toast.BatteryPanel.IsVisible = false;
        toast.DisconnectPanel.IsVisible = true;
        toast.DisconnectTitle.Text = deviceName;
        await ShowAndClose(toast, 3000);
    }

    private bool _registered;

    /// <summary>播放出现动画：滑入(translateX 28->0) + 淡入(Opacity 0->1)。</summary>
    private void PlayEnter()
    {
        Opacity = 1;
        Card.RenderTransform = TransformOperations.Parse("translateX(0px)");
    }

    /// <summary>播放消失动画：滑出(0->28) + 淡出(1->0)，等过渡结束再关。</summary>
    private async Task PlayExitAndCloseAsync()
    {
        Opacity = 0;
        Card.RenderTransform = TransformOperations.Parse("translateX(28px)");
        await Task.Delay(400);
        Close();
    }

    private static async Task ShowAndClose(ToastWindow toast, int durationMs)
    {
        ApplyTheme(toast);
        // 初始态(透明+右移)已由 XAML 声明，从第 0 帧生效
        toast.Closed += (_, _) => ToastManager.Unregister(toast);
        toast.Show();

        // 宽度固定(320)，仅高度随内容变化；等布局稳定拿到真实高度后注册定位
        toast.LayoutUpdated += (_, _) =>
        {
            if (toast._registered) return;
            if (toast.Bounds.Height <= 1) return;
            toast._registered = true;
            ToastManager.Register(toast);   // 定位（右下角、不重叠）
        };

        // 等布局稳定（拿到真实高度并完成定位）
        for (int i = 0; i < 20 && !toast._registered; i++)
            await Task.Delay(50);
        if (!toast._registered)
        {
            toast._registered = true;
            ToastManager.Register(toast);
        }

        // 关键：确保初始态(透明+右移)已实际绘制若干帧后再设目标值。
        // 首个窗口合成器冷启动较慢，等两拍渲染，避免第一个 Toast 动画没有基线而跳变。
        await WaitFramesAsync(toast, 2);
        toast.PlayEnter();              // 触发滑入+淡入

        await Task.Delay(durationMs);
        await Dispatcher.UIThread.InvokeAsync(toast.PlayExitAndCloseAsync);
    }

    /// <summary>等待 n 次实际渲染帧（用合成器帧回调，比固定延时更可靠）。</summary>
    private static async Task WaitFramesAsync(ToastWindow toast, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var tcs = new TaskCompletionSource();
            var top = Avalonia.Controls.TopLevel.GetTopLevel(toast);
            if (top == null) { await Task.Delay(16); continue; }
            top.RequestAnimationFrame(_ => tcs.TrySetResult());
            // 兜底超时，防止极端情况下帧回调不触发导致卡住
            var timeout = Task.Delay(100);
            await Task.WhenAny(tcs.Task, timeout);
        }
    }

    /// <summary>百分比数字保持干净；充电用标签行的小闪电提示，避免大字溢出 pill。</summary>
    private static void SetBat(TextBlock pct, Control bolt, (int Lvl, bool Chg)? d)
    {
        if (d is not { } v) { pct.Text = "- %"; bolt.IsVisible = false; return; }
        pct.Text = $"{v.Lvl}%";
        bolt.IsVisible = v.Chg;
    }

    private static (int, bool)? TryGet(System.Collections.Concurrent.ConcurrentDictionary<string, (int, bool)?> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v : null;

    /// <summary>让 Toast 跟随 App 深浅主题。</summary>
    private static void ApplyTheme(ToastWindow toast)
    {
        var theme = SukiTheme.GetInstance();
        var isLight = theme.ActiveBaseTheme == Avalonia.Styling.ThemeVariant.Light;

        if (isLight)
        {
            toast.Card.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            toast.Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0x00, 0x00, 0x00));
            var fg = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var fgMuted = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            toast.TitleBlock.Foreground = fg;
            toast.LeftPct.Foreground = fg; toast.LeftLabel.Foreground = fgMuted;
            toast.RightPct.Foreground = fg; toast.RightLabel.Foreground = fgMuted;
            toast.CasePct.Foreground = fg; toast.CaseLabel.Foreground = fgMuted;
        }
    }
}
