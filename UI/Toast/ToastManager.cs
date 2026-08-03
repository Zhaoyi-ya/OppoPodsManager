using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.UI.Toast;

/// <summary>
/// 桌面右下角 Toast 堆叠管理器：保证多个 Toast（如"已连接"+"已断开"）不重叠，
/// 统一自底向上垂直排布。所有方法须在 UI 线程调用。
/// 定位按物理像素计算（含 DPI 缩放），避免高分屏落点偏移。
/// </summary>
internal static class ToastManager
{
    // 紧贴桌面右下角：窗口边缘贴住工作区角，卡片因窗口内 16px 阴影留白自然离角 16px（留给投影）
    private const double MarginRight = 0;    // 距屏幕右边（DIP）
    private const double MarginBottom = 0;   // 距工作区底边（DIP）
    private const double Gap = 4;             // Toast 之间的间隔（DIP，两窗各有 16px 阴影留白，实际间距约 36px）

    private const int MaxActiveToasts = 2;
    private static readonly List<ToastWindow> _active = new();

    /// <summary>注册一个已完成布局的 Toast（新的排在最下，旧的上移）。</summary>
    public static void Register(ToastWindow toast)
    {
        if (!_active.Contains(toast))
            _active.Add(toast);

        while (_active.Count > MaxActiveToasts)
        {
            var old = _active[0];
            _active.RemoveAt(0);
            old.Close();
        }

        Reposition();
    }

    /// <summary>注销一个已关闭的 Toast，并重排其余。</summary>
    public static void Unregister(ToastWindow toast)
    {
        if (_active.Remove(toast)) Reposition();
    }

    /// <summary>自底向上重新排布所有活动 Toast。</summary>
    private static void Reposition()
    {
        // 从最后（最新）一个开始贴底，依次向上堆叠
        for (int i = 0; i < _active.Count; i++)
        {
            var toast = _active[i];
            var screen = toast.Screens?.Primary;
            if (screen == null) continue;

            double scale = toast.RenderScaling <= 0 ? 1.0 : toast.RenderScaling;
            var wa = screen.WorkingArea;  // 物理像素

            double wPx = toast.Bounds.Width * scale;
            double hPx = toast.Bounds.Height * scale;
            if (wPx <= 1 || hPx <= 1) continue;

            // 累计本条下方所有 Toast 的高度（含间隔），得到本条底边上移量
            double stackedBelowPx = 0;
            for (int j = i + 1; j < _active.Count; j++)
                stackedBelowPx += _active[j].Bounds.Height * scale + Gap * scale;

            double x = wa.Right - wPx - MarginRight * scale;
            double y = wa.Bottom - hPx - MarginBottom * scale - stackedBelowPx;
            toast.Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        }
    }
}

// 监听 Next 状态快照并沿用原项目的 Toast 生命周期和堆叠逻辑。
public sealed class ToastNotificationService : IDisposable
{
    private readonly FrontendState _frontendState;
    private readonly SettingsManager? _settings;
    private BusinessSnapshot _previous;

    public ToastNotificationService(FrontendState frontendState, SettingsManager? settings = null)
    {
        _frontendState = frontendState;
        _settings = settings;
        _previous = frontendState.Snapshot;
        _frontendState.Changed += OnStateChanged;
    }

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        var previous = _previous;
        _previous = snapshot;
        if (previous.IsConnected != snapshot.IsConnected)
        {
            ApplicationLog.Current?.Info("Toast", $"连接状态变化：before={previous.IsConnected}，after={snapshot.IsConnected}。");
            _ = ToastWindow.ShowSnapshotAsync(snapshot, GetDeviceName(snapshot),
                snapshot.IsConnected ? ToastType.Battery : ToastType.Disconnected,
                GetDuration(), _settings);
            return;
        }

        if (!snapshot.IsConnected)
            return;

        var previousLevel = LowestBattery(previous);
        var currentLevel = LowestBattery(snapshot);
        if (currentLevel is null || currentLevel > 20 || previousLevel is not null && previousLevel <= 20)
            return;

        ApplicationLog.Current?.Info("Toast", $"低电量通知：before={previousLevel?.ToString() ?? ""}，current={currentLevel}。");
        _ = ToastWindow.ShowSnapshotAsync(snapshot, GetDeviceName(snapshot),
            currentLevel <= 5 ? ToastType.CriticalBattery : ToastType.LowBattery,
            GetDuration(), _settings);
    }

    // Toast 使用型号优先的名称，保持与主窗口、托盘和小窗口一致。
    private static string GetDeviceName(BusinessSnapshot snapshot)
        => DeviceText.DeviceName(snapshot.Identity?.ModelName, snapshot.DeviceName);

    private static byte? LowestBattery(BusinessSnapshot snapshot)
    {
        var levels = new[] { snapshot.LeftBattery?.Percent, snapshot.RightBattery?.Percent, snapshot.CaseBattery?.Percent }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return levels.Length == 0 ? null : levels.Min();
    }

    private int GetDuration()
        => Math.Clamp(_settings?.Current.ToastDurationSeconds ?? 5, 3, 8) * 1000;

    public void Dispose() => _frontendState.Changed -= OnStateChanged;
}
