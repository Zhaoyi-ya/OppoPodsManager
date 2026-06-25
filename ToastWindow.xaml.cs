using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace OppoPodsWPF;

public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
    }

    /// <summary>正常连接电量提示（显示 5 秒）</summary>
    public static Task ShowAsync(PodState state, string deviceName) =>
        ShowCoreAsync(state, deviceName, lowBattery: false);

    /// <summary>低电量提醒：覆盖层显示「⚡ 低电量」2 秒后淡化，露出电量信息 3 秒</summary>
    public static Task ShowLowBatteryAsync(PodState state, string deviceName) =>
        ShowCoreAsync(state, deviceName, lowBattery: true);

    /// <summary>极低电量提醒（≤10%），红色警告</summary>
    public static Task ShowCriticalBatteryAsync(PodState state, string deviceName) =>
        ShowCoreAsync(state, deviceName, lowBattery: false, critical: true);

    /// <summary>断连提示（显示 3 秒）</summary>
    public static Task ShowDisconnectedAsync(string deviceName) =>
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var toast = new ToastWindow();
            toast.BatteryPanel.Visibility = Visibility.Collapsed;
            toast.DisconnectPanel.Visibility = Visibility.Visible;
            toast.DisconnectTitle.Text = deviceName;

            toast.WindowStartupLocation = WindowStartupLocation.Manual;
            toast.Opacity = 0;

            var tcs = new TaskCompletionSource();
            toast.ContentRendered += (_, _) =>
            {
                var sw = SystemParameters.WorkArea.Width;
                var sh = SystemParameters.WorkArea.Height;
                toast.Left = sw - toast.ActualWidth - 16;
                toast.Top = sh - toast.ActualHeight - 16;
                tcs.TrySetResult();
            };

            toast.Show();
            await tcs.Task;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            toast.BeginAnimation(OpacityProperty, fadeIn);

            await Task.Delay(3000);

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => toast.Close();
            toast.BeginAnimation(OpacityProperty, fadeOut);
        }).Task;

    private static async Task ShowCoreAsync(PodState state, string deviceName, bool lowBattery, bool critical = false)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var toast = new ToastWindow();
            toast.TitleBlock.Text = deviceName;

            SetBat(toast.LeftPct, TryGet(state.Battery, "L"));
            SetBat(toast.RightPct, TryGet(state.Battery, "R"));
            SetBat(toast.CasePct, TryGet(state.Battery, "C"));

            toast.WindowStartupLocation = WindowStartupLocation.Manual;
            toast.Opacity = 0;

            if (lowBattery)
                toast.LowBatteryOverlay.Visibility = Visibility.Visible;
            else if (critical)
                toast.CriticalBatteryOverlay.Visibility = Visibility.Visible;

            var tcs = new TaskCompletionSource();
            toast.ContentRendered += (_, _) =>
            {
                var sw = SystemParameters.WorkArea.Width;
                var sh = SystemParameters.WorkArea.Height;
                toast.Left = sw - toast.ActualWidth - 16;
                toast.Top = sh - toast.ActualHeight - 16;
                tcs.TrySetResult();
            };

            toast.Show();
            await tcs.Task;

            // 淡入
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            toast.BeginAnimation(OpacityProperty, fadeIn);

            if (lowBattery || critical)
            {
                var overlay = lowBattery ? toast.LowBatteryOverlay : toast.CriticalBatteryOverlay;
                // 阶段 1：覆盖层显示警告 2 秒
                await Task.Delay(2000);
                // 阶段 2：覆盖层淡出，露出下方电量
                var overlayFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                overlayFade.Completed += (_, _) => overlay.Visibility = Visibility.Collapsed;
                overlay.BeginAnimation(OpacityProperty, overlayFade);
                await Task.Delay(2750);
            }
            else
            {
                await Task.Delay(5000);
            }

            // 整体淡出关闭
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => toast.Close();
            toast.BeginAnimation(OpacityProperty, fadeOut);
        });
    }

    private static void SetBat(System.Windows.Controls.TextBlock tb, (int Lvl, bool Chg)? d)
    {
        if (d is not { } v) { tb.Text = "- %"; return; }
        tb.Text = $"{v.Lvl}%{(v.Chg ? " ⚡" : "")}";
    }

    private static (int, bool)? TryGet(Dictionary<string, (int, bool)?> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v : null;
}
