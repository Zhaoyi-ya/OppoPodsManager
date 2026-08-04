using System;
using System.Threading.Tasks;
using Avalonia.Media.Transformation;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using OppoPodsManager.Assets.Localization;
using NextSettingsManager = OppoPodsManager.Assets.UserSettings.SettingsManager;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;
using SukiUI;
using AvaloniaControl = Avalonia.Controls.Control;

namespace OppoPodsManager.UI.Toast;

public enum ToastType { Battery, LowBattery, CriticalBattery, Disconnected }
public enum UpdateToastAction { Later, Skip, MirrorDownload, Download }

public partial class ToastWindow : Window
{
    private static readonly TransformOperations EnterTransform = TransformOperations.Parse("translateX(0px)");
    private static readonly TransformOperations ExitTransform = TransformOperations.Parse("translateX(28px)");
    private static readonly SolidColorBrush LightCardBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private static readonly SolidColorBrush LightBorderBrush = new(Color.FromArgb(0x15, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush LightPillBrush = new(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush DarkCardBrush = new(Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush DarkPillBrush = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LightTextBrush = new(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly SolidColorBrush LightMutedTextBrush = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush LightCriticalTextBrush = new(Color.FromRgb(0x99, 0x45, 0x3A));
    private TaskCompletionSource<UpdateToastAction>? _updateActionTcs;

    public ToastWindow()
    {
        InitializeComponent();
        UpdateCloseBtn.Click += (_, _) => CompleteUpdateAction(UpdateToastAction.Later);
        UpdateLaterBtn.Click += (_, _) => CompleteUpdateAction(UpdateToastAction.Later);
        UpdateSkipBtn.Click += (_, _) => CompleteUpdateAction(UpdateToastAction.Skip);
        UpdateMirrorBtn.Click += (_, _) => CompleteUpdateAction(UpdateToastAction.MirrorDownload);
        UpdateDownloadBtn.Click += (_, _) => CompleteUpdateAction(UpdateToastAction.Download);
        // 闪电图标向量（替代 ⚡ 避免 MiSans 缺失显示为方框）
        var boltGeo = StreamGeometry.Parse("M0.009,7.21C-0.023,7.286 0.032,7.37 0.115,7.37H3.303V11.885C3.303,12.011 3.476,12.045 3.524,11.929L6.6,4.471C6.631,4.396 6.575,4.313 6.494,4.313H3.303V0.115C3.303,-0.01 3.132,-0.045 3.083,0.069L0.009,7.21Z");
        LeftBolt.Data = boltGeo;
        CaseBolt.Data = boltGeo;
        RightBolt.Data = boltGeo;
        LowBolt.Data = boltGeo;
        CritBolt.Data = boltGeo;
    }

    // 使用 Next 控制层快照显示 Toast，保持原项目窗口布局和动画不变。
    public static Task ShowSnapshotAsync(BusinessSnapshot? snapshot, string deviceName,
        ToastType type = ToastType.Battery, int durationMs = 5000, NextSettingsManager? settings = null)
        => RunOnUiThreadAsync(() => ShowSnapshotCoreAsync(snapshot, deviceName, type, durationMs, settings));

    // 在 UI 线程创建窗口并执行 Toast 的完整显示生命周期。
    private static async Task ShowSnapshotCoreAsync(BusinessSnapshot? snapshot, string deviceName,
        ToastType type, int durationMs, NextSettingsManager? settings)
    {
        ApplicationLog.Current?.Debug("Toast", $"显示 Next 快照 Toast：type={type}，device={deviceName}，revision={snapshot?.Revision.ToString() ?? ""}。");
        var toast = new ToastWindow();
        if (type == ToastType.Disconnected)
        {
            toast.BatteryPanel.IsVisible = false;
            toast.DisconnectPanel.IsVisible = true;
            toast.DisconnectTitle.Text = deviceName;
        }
        else
        {
            toast.TitleBlock.Text = deviceName;
            SetNextBattery(toast.LeftPct, toast.LeftBolt, snapshot?.LeftBattery);
            SetNextBattery(toast.RightPct, toast.RightBolt, snapshot?.RightBattery);
            SetNextBattery(toast.CasePct, toast.CaseBolt, snapshot?.CaseBattery);
            toast.LowBatteryOverlay.IsVisible = type == ToastType.LowBattery;
            toast.CriticalBatteryOverlay.IsVisible = type == ToastType.CriticalBattery;
        }

        if (type is ToastType.LowBattery or ToastType.CriticalBattery)
        {
            var overlay = type == ToastType.LowBattery ? toast.LowBatteryOverlay : toast.CriticalBatteryOverlay;
            await ShowAndClose(toast, async () =>
            {
                var holdMs = Math.Min(2000, Math.Max(800, durationMs - 1500));
                await Task.Delay(holdMs);
                overlay.Opacity = 0;
                await Task.Delay(Math.Max(500, durationMs - holdMs));
            }, settings);
        }
        else
        {
            await ShowAndClose(toast, () => Task.Delay(durationMs), settings);
        }
    }

    // 将 Next 电量模型转换为 Toast 控件所需的显示值。
    private static void SetNextBattery(TextBlock percentage, AvaloniaControl bolt, BatteryLevel? battery)
    {
        percentage.Text = battery is { } value ? $"{value.Percent}%" : "- %";
        bolt.IsVisible = battery?.IsCharging == true;
    }

    public static Task<UpdateToastAction> ShowUpdateAsync(string version, int durationMs = 10000)
        => RunOnUiThreadAsync(() => ShowUpdateCoreAsync(version, durationMs));

    // 在 UI 线程创建更新 Toast，保证按钮和动画访问不跨线程。
    private static async Task<UpdateToastAction> ShowUpdateCoreAsync(string version, int durationMs)
    {
        ApplicationLog.Current?.Debug("UI", $"Toast: 显示更新提示 version={version} duration={durationMs}ms");
        var toast = new ToastWindow
        {
            _updateActionTcs = new TaskCompletionSource<UpdateToastAction>()
        };
        toast.BatteryPanel.IsVisible = false;
        toast.DisconnectPanel.IsVisible = false;
        toast.LowBatteryOverlay.IsVisible = false;
        toast.CriticalBatteryOverlay.IsVisible = false;
        toast.UpdatePanel.IsVisible = true;
        toast.UpdateTitle.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Toast_NewVersion);
        toast.UpdateVersion.Text = string.Format(
            LanguageManager.Instance.GetString(LanguageManager.Instance.Toast_VersionLabel),
            NormalizeVersionLabel(version));

        await ShowAndClose(toast, async () =>
        {
            var completed = await Task.WhenAny(toast._updateActionTcs.Task, Task.Delay(durationMs));
            if (completed != toast._updateActionTcs.Task)
                toast._updateActionTcs.TrySetResult(UpdateToastAction.Later);
        });

        return await toast._updateActionTcs.Task;
    }

    // 将后台通知线程上的 Toast 请求安全转发到 Avalonia UI 线程。
    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<object?>
            (TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    // 将返回用户操作结果的 Toast 请求安全转发到 Avalonia UI 线程。
    private static Task<TResult> RunOnUiThreadAsync<TResult>(Func<Task<TResult>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<TResult>
            (TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private void CompleteUpdateAction(UpdateToastAction action)
    {
        ApplicationLog.Current?.Debug("UI", $"Toast: 更新提示操作 -> {action}");
        _updateActionTcs?.TrySetResult(action);
    }

    private static string NormalizeVersionLabel(string version)
        => version.StartsWith('v') || version.StartsWith('V') ? version : $"v{version}";

    private bool _registered;

    /// <summary>播放出现动画：滑入(translateX 28->0) + 淡入(Opacity 0->1)。</summary>
    private void PlayEnter()
    {
        Opacity = 1;
        Card.RenderTransform = EnterTransform;
    }

    /// <summary>播放消失动画：滑出(0->28) + 淡出(1->0)，等过渡结束再关。</summary>
    private async Task PlayExitAndCloseAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(PlayExitAndCloseAsync);
            return;
        }

        Opacity = 0;
        Card.RenderTransform = ExitTransform;
        await Task.Delay(400);
        Close();
    }

    private static async Task ShowAndClose(ToastWindow toast, Func<Task> onEntered, NextSettingsManager? settings = null)
    {
        ApplyTheme(toast, settings);
        // 初始态(透明+右移)已由 XAML 声明，从第 0 帧生效
        EventHandler? layoutUpdatedHandler = null;
        EventHandler? closedHandler = null;
        closedHandler = (_, _) =>
        {
            toast.Closed -= closedHandler;
            if (layoutUpdatedHandler != null)
                toast.LayoutUpdated -= layoutUpdatedHandler;
            ToastManager.Unregister(toast);
        };
        toast.Closed += closedHandler;
        toast.Show();

        // 宽度固定(360)，仅高度随内容变化；等布局稳定拿到真实高度后注册定位
        layoutUpdatedHandler = (_, _) =>
        {
            if (toast._registered) return;
            if (toast.Bounds.Height <= 1) return;
            toast._registered = true;
            toast.LayoutUpdated -= layoutUpdatedHandler;
            ToastManager.Register(toast);   // 定位（右下角、不重叠）
        };
        toast.LayoutUpdated += layoutUpdatedHandler;

        // 等布局稳定（拿到真实高度并完成定位）
        for (int i = 0; i < 20 && !toast._registered; i++)
            await Task.Delay(50);
        if (!toast._registered)
        {
            toast._registered = true;
            toast.LayoutUpdated -= layoutUpdatedHandler;
            ToastManager.Register(toast);
        }

        // 关键：确保初始态(透明+右移)已实际绘制若干帧后再设目标值。
        // 首个窗口合成器冷启动较慢，等两拍渲染，避免第一个 Toast 动画没有基线而跳变。
        await WaitFramesAsync(toast, 2);
        toast.PlayEnter();              // 触发滑入+淡入

        await onEntered();
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

    /// <summary>让 Toast 跟随 App 深浅主题。</summary>
    private static void ApplyTheme(ToastWindow toast, NextSettingsManager? settings = null)
    {
        var theme = SukiTheme.GetInstance();
        var activeTheme = theme.ActiveBaseTheme == Avalonia.Styling.ThemeVariant.Default
            ? Avalonia.Application.Current?.ActualThemeVariant
            : theme.ActiveBaseTheme;
        var isLight = activeTheme == Avalonia.Styling.ThemeVariant.Light;
        var transparencyPct = settings is null
            ? 50
            : Math.Clamp(settings.Current.CardOpacity, 0, 90);
        var alpha = (byte)Math.Clamp(255 - (transparencyPct * 255 / 100), 25, 255);

        if (isLight)
        {
            LightCardBrush.Color = Color.FromArgb(alpha, 0xF5, 0xF5, 0xF5);
            LightPillBrush.Color = Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
            toast.Card.Background = LightCardBrush;
            toast.Card.BorderBrush = LightBorderBrush;
            toast.LeftPill.Background = LightPillBrush;
            toast.CasePill.Background = LightPillBrush;
            toast.RightPill.Background = LightPillBrush;
            var fg = LightTextBrush;
            var fgMuted = LightMutedTextBrush;
            toast.TitleBlock.Foreground = fg;
            toast.LeftPct.Foreground = fg; toast.LeftLabel.Foreground = fgMuted;
            toast.RightPct.Foreground = fg; toast.RightLabel.Foreground = fgMuted;
            toast.CasePct.Foreground = fg; toast.CaseLabel.Foreground = fgMuted;

            // 断开面板设备名
            toast.DisconnectTitle.Foreground = fg;

            // 更新提示面板
            toast.UpdateTitle.Foreground = fg;
            toast.UpdateVersion.Foreground = fgMuted;
            toast.UpdateLaterBtn.Foreground = fg;
            toast.UpdateSkipBtn.Foreground = fg;
            toast.UpdateMirrorBtn.Foreground = fg;
            toast.UpdateDownloadBtn.Foreground = fg;
            toast.UpdateClosePath.Stroke = fg;
            toast.UpdateLaterBtn.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
            toast.UpdateSkipBtn.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
            toast.UpdateMirrorBtn.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
            toast.UpdateDownloadBtn.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0x00, 0x00, 0x00));

            // 遮罩背景：浅色模式用浅灰
            toast.LowBatteryOverlay.Background = LightCardBrush;
            toast.CriticalBatteryOverlay.Background = LightCardBrush;

            // 遮罩提示文字：浅色模式用深色
            toast.LowHintText.Foreground = fgMuted;
            toast.CritHintText.Foreground = LightCriticalTextBrush;
        }
        else
        {
            var glassAlpha = (byte)Math.Clamp(alpha * 0.35, 9, 255);
            DarkCardBrush.Color = Color.FromArgb(glassAlpha, 0x1C, 0x1C, 0x1E);
            DarkPillBrush.Color = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
            toast.Card.Background = DarkCardBrush;
            toast.Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            toast.LeftPill.Background = DarkPillBrush;
            toast.CasePill.Background = DarkPillBrush;
            toast.RightPill.Background = DarkPillBrush;
        }
    }
}
