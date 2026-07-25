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
using Avalonia.VisualTree;
using OppoPodsManager.Localization;
using CoreCaps = OppoPodsManager.Core.Devices.DeviceCapabilities;
using CoreMulti = OppoPodsManager.Core.Devices.MultiDeviceEntry;

namespace OppoPodsManager;

public partial class MainWindow
{
    // ===== 设备列表 =====
    // 记录上次渲染的列表签名：SyncMultiDeviceList 会被每次 StateChanged（含电量/ANC 轮询，约2s一次）触发，
    // 若每次都 Clear+重建，正打开的右键菜单会因宿主 Border 被销毁而闪退，并让 Avalonia 样式/视觉对象持续堆积。
    // 签名只保留设备行结构与菜单结构字段；音频活动等高频字段不再触发整树重建。
    private string _deviceListSignature = "";
    private bool _deviceListUpdatePending;
    private readonly Dictionary<string, DeviceListRowRefs> _deviceListRows = new();

    private sealed class DeviceListRowRefs
    {
        public required Border Root { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock AudioText { get; init; }
        public required TextBlock StatusText { get; init; }
    }

    private void CloseOpenDeviceContextMenus()
    {
        foreach (var row in _deviceListRows.Values)
        {
            if (row.Root.ContextMenu is { } menu && menu.IsOpen)
                menu.Close();
        }
    }

    private void RequestSyncMultiDeviceList()
    {
        if (_deviceListUpdatePending)
            return;

        _deviceListUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _deviceListUpdatePending = false;
            SyncMultiDeviceList();
        });
    }

    private void CloseMultiDeviceMenuOnOutsideClick(object? sender, PointerPressedEventArgs e)
    {
        _openMultiDeviceMenu?.Close();
    }

    private void SyncMultiDeviceList()
    {
        if (!IsStateConnected(_pods.State))
        {
            const string disconnectedSignature = "##disconnected";
            if (_deviceListSignature == disconnectedSignature && DeviceList.Items.Count == 0)
                return;

            _deviceListSignature = disconnectedSignature;
            foreach (var item in DeviceList.Items.OfType<Control>())
            {
                if (item.ContextMenu is { } menu && menu.IsOpen)
                    menu.Close();
                item.ContextMenu = null;
            }
            DeviceList.Items.Clear();
            _deviceListRows.Clear();
            DeviceListEmptyHint.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_EmptyHint);
            DeviceListEmptyHint.IsVisible = true;
            DiConnectionStrategyCard.IsVisible = false;
            UpdateDeviceListStatus(Array.Empty<CoreMulti>());
            return;
        }

        var hiddenMacs = SettingsManager.GetHiddenMultiDeviceMacs();
        var all = _pods.State.MultiDevice.Devices
            .Where(device => device.IsCurrentDevice || !hiddenMacs.Contains(device.Address))
            .ToList();
        var caps = _modelOverride != null
            ? _controller.ForceModel(_modelOverride)
            : _pods.Caps;
        var canManage = caps.IsMultiConnectV2
            && caps.CanUnpairMultiConnectDevice(_pods.State.SupportedCommands);
        // Priority strategy UI is driven by 0x8132 readback, not only by 0x0429 manage ops.
        var canManagePriority = canManage
            || caps.HasDualDevice
            || caps.HasMultiConnectManage;
        var canUnpair = caps.CanUnpairMultiConnectDevice(_pods.State.SupportedCommands);
        SyncConnectionStrategy(caps, canManagePriority, all);
        DeviceListEmptyHint.Text = hiddenMacs.Count > 0
            ? LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_AllHidden)
            : LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_NoOtherDevices);
        DeviceListEmptyHint.IsVisible = all.Count == 0;

        // 列表签名包含连接策略回读字段：0x8132 只改 auto/priority 时也必须刷新策略 UI。
        var sig = string.Join("|", all.Select(d =>
            $"{d.Address};{(d.Name ?? d.Address)};{d.ConnectionState};{d.IsCurrentDevice}"))
            + $"##manage={canManage};unpair={canUnpair};hidden={string.Join(',', hiddenMacs.OrderBy(value => value))};conn={IsStateConnected(_pods.State)}"
            + $";prioAuto={_pods.State.MultiDevice.AutomaticPriority};prio={_pods.State.MultiDevice.PriorityAddress}";
        if (sig == _deviceListSignature && DeviceList.Items.Count > 0)
        {
            UpdateDeviceListRows(all);
            UpdateDeviceListStatus(all);
            // 结构未变时也再刷一次策略控件，避免 0x8132 晚于列表到达时选中项卡住。
            SyncConnectionStrategy(caps, canManagePriority, all);
            return;
        }
        _deviceListSignature = sig;
        Log.D("UI", "SyncMultiDeviceList: 渲染 " + all.Count + " 个设备: " + string.Join(", ", all.Select(d => $"{(d.Name ?? d.Address)}/{d.Address}/{d.ConnectionState}/cur={d.IsCurrentDevice}")));

        foreach (var item in DeviceList.Items.OfType<Control>())
        {
            if (item.ContextMenu is { } menu && menu.IsOpen)
                menu.Close();
            item.ContextMenu = null;
        }
        DeviceList.Items.Clear();
        _deviceListRows.Clear();

        foreach (var d in all)
        {
            // 连接状态圆点
            var dotColor = d.ConnectionState switch { 2 => BrushGreen, 1 => BrushGray, _ => BrushRed };
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Fill = dotColor };

            // 设备名
            var nameTb = new TextBlock
            {
                Text = (d.Name ?? d.Address),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 112,
                Foreground = d.IsCurrentDevice ? BrushLightGreen : BrushWhite
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3) };
            row.Children.Add(dot);
            row.Children.Add(nameTb);

            // 音频活动指示。始终保留控件，后续状态变化只改文本/可见性，避免高频重建视觉树。
            var audioHint = new TextBlock
            {
                Text = GetDeviceAudioText(d),
                FontSize = 12,
                Opacity = d.IsCurrentDevice ? 0.5 : 0.6,
                Foreground = d.IsCurrentDevice ? BrushLightGreen : BrushGreen,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = d.IsAudioActive
            };
            row.Children.Add(audioHint);

            // 连接状态文字。始终保留控件，状态变化只切换可见性。
            var status = new TextBlock
            {
                Text = GetDeviceConnectionText(d),
                FontSize = 11,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = d.ConnectionState != 2 && !d.IsCurrentDevice
            };
            row.Children.Add(status);

            var border = new Border { Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(4), Child = row };
            if (d.IsCurrentDevice)
                border.Background = _deviceCurrentBgBrush;

            _deviceListRows[GetDeviceListRowKey(d)] = new DeviceListRowRefs
            {
                Root = border,
                Dot = dot,
                NameText = nameTb,
                AudioText = audioHint,
                StatusText = status
            };

            // 右键菜单 / 左键操作
            var menu = new ContextMenu();
            var isReal = !string.IsNullOrEmpty(d.Address) && d.Address != "current";

            if (isReal && !d.IsCurrentDevice)
            {
                if (d.ConnectionState != 2)
                {
                    // 已断开 → 连接
                    var connect = new MenuItem { Header = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Connect), (d.Name ?? d.Address)) };
                    connect.Click += (_, _) => _controller.MultiDeviceConnect(d.Address);
                    menu.Items.Add(connect);
                }
                else
                {
                    var disconnect = new MenuItem { Header = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Disconnect), (d.Name ?? d.Address)) };
                    disconnect.Click += (_, _) => _controller.MultiDeviceDisconnect(d.Address);
                    menu.Items.Add(disconnect);
                }
                if (canUnpair)
                {
                    // Melody 仅在 V2 多设备管理页提供解绑，协议为 0x0429 operation=3。
                    menu.Items.Add(new Separator());
                    var unpair = new MenuItem { Header = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Unpair) };
                    unpair.Click += (_, _) => _controller.MultiDeviceUnpair(d.Address);
                    menu.Items.Add(unpair);
                }
                menu.Items.Add(new Separator());
                var hide = new MenuItem { Header = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Hide) };
                hide.Click += (_, _) => HideMultiDevice(d);
                menu.Items.Add(hide);
            }
            else if (isReal && d.IsCurrentDevice)
            {
                var disconnect = new MenuItem { Header = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Disconnect), (d.Name ?? d.Address)) };
                disconnect.Click += (_, _) => _controller.MultiDeviceDisconnect(d.Address);
                menu.Items.Add(disconnect);
            }

            if (menu.Items.Count > 0)
            {
                menu.Opened += (_, _) => _openMultiDeviceMenu = menu;
                menu.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_openMultiDeviceMenu, menu))
                        _openMultiDeviceMenu = null;
                };
                border.ContextMenu = menu;
                border.Cursor = new Cursor(StandardCursorType.Hand);

                // 左键快捷操作：已断开 → 连接；已连接非当前 → 设为优先设备。
                border.PointerPressed += (s, e) =>
                {
                    var pt = e.GetCurrentPoint(border);
                    if (!pt.Properties.IsLeftButtonPressed) return;
                    if (d.IsCurrentDevice)
                    {
                        // 当前设备（本机）：设为优先设备
                        if (canManage)
                        {
                            Log.D("UI", $"设为优先设备(本机) -> {(d.Name ?? d.Address)} ({d.Address})");
                            _controller.MultiDeviceSetPriority(d.Address);
                        }
                    }
                    else if (d.ConnectionState != 2)
                    {
                        Log.D("UI", $"连接设备 -> {(d.Name ?? d.Address)} ({d.Address})");
                        _controller.MultiDeviceConnect(d.Address);
                    }
                    else if (canManage)
                    {
                        Log.D("UI", $"设为优先设备 -> {(d.Name ?? d.Address)} ({d.Address})");
                        _controller.MultiDeviceSetPriority(d.Address);
                    }
                };
            }

            DeviceList.Items.Add(border);
        }

        UpdateDeviceListStatus(all);
    }

    private void SyncConnectionStrategy(
        CoreCaps caps,
        bool canManagePriority,
        IReadOnlyCollection<CoreMulti> visibleDevices)
    {
        // Whitelist capability is enough to show the control; state comes from 0x8132.
        var showStrategy = canManagePriority;
        DiConnectionStrategyCard.IsVisible = showStrategy;
        PriorityDevicePanel.IsVisible = canManagePriority;

        _syncingConnectionStrategy = true;
        try
        {
            if (!canManagePriority)
            {
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.SelectedItem = null;
                _priorityOptionsSignature = "";
                return;
            }

            var connected = visibleDevices
                .Where(device => device.ConnectionState == 2
                    && !string.IsNullOrWhiteSpace(device.Address))
                .ToList();
            // Include strategy mode in option signature so selection is recomputed after 0x8132.
            var optionSignature = string.Join("|", connected.Select(device =>
                $"{device.Address};{(device.Name ?? device.Address)};{device.IsCurrentDevice}"))
                + $"|mode={_pods.State.MultiDevice.AutomaticPriority}|prio={_pods.State.MultiDevice.PriorityAddress}";
            if (optionSignature != _priorityOptionsSignature)
            {
                _priorityOptionsSignature = optionSignature;
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.Items.Add(new PriorityDeviceOption
                {
                    IsAutomatic = true,
                    DisplayName = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Automatic),
                });

                foreach (var device in connected)
                {
                    var name = string.IsNullOrWhiteSpace((device.Name ?? device.Address))
                        ? LanguageManager.Instance.GetString(LanguageManager.Instance.Common_UnknownDevice)
                        : (device.Name ?? device.Address);
                    CbPriorityDevice.Items.Add(new PriorityDeviceOption
                    {
                        Address = device.Address,
                        DisplayName = name,
                    });
                }
            }

            PriorityDeviceOption? selected = null;
            if (_pods.State.MultiDevice.AutomaticPriority || string.IsNullOrWhiteSpace(_pods.State.MultiDevice.PriorityAddress))
            {
                // Auto mode, or fixed mode with no valid address → show "自动选择".
                selected = CbPriorityDevice.Items
                    .OfType<PriorityDeviceOption>()
                    .FirstOrDefault(option => option.IsAutomatic);
                CbPriorityDevice.PlaceholderText = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Automatic);
            }
            else
            {
                var priorityAddr = _pods.State.MultiDevice.PriorityAddress;

                selected = CbPriorityDevice.Items
                    .OfType<PriorityDeviceOption>()
                    .FirstOrDefault(option => string.Equals(
                        option.Address,
                        priorityAddr,
                        StringComparison.OrdinalIgnoreCase));
                CbPriorityDevice.PlaceholderText = selected == null
                    ? LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityUnavailable)
                    : LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityHint);
            }
            CbPriorityDevice.SelectedItem = selected;
        }
        finally
        {
            _syncingConnectionStrategy = false;
        }

        var pending = _pendingPrioritySelection;
        _pendingPrioritySelection = null;
        if (pending != null && pending != CbPriorityDevice.SelectedItem)
        {
            CbPriorityDevice.SelectedItem = pending;
        }
    }

    private void CbPriorityDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_pods.IsConnected
            || CbPriorityDevice.SelectedItem is not PriorityDeviceOption option)
            return;

        if (_syncingConnectionStrategy)
        {
            _pendingPrioritySelection = option;
            return;
        }

        ApplyPrioritySelection(option);
    }

    private void ApplyPrioritySelection(PriorityDeviceOption option)
    {
        if (option.IsAutomatic)
            _controller.MultiDeviceAutoSwitch();
        else
            _controller.MultiDeviceSetPriority(option.Address);
    }

    private void HideMultiDevice(CoreMulti device)
    {
        if (device.IsCurrentDevice || string.IsNullOrWhiteSpace(device.Address)) return;
        SettingsManager.HideMultiDevice(device.Address);
        _deviceListSignature = "";
        SyncMultiDeviceList();
        RefreshRestoreHiddenDevicesButton();
        Log.D("UI", $"本地隐藏多设备 addr={device.Address}");
    }

    private void RefreshRestoreHiddenDevicesButton()
    {
        var count = SettingsManager.GetHiddenMultiDeviceMacs().Count;
        BtnRestoreHiddenDevices.IsEnabled = count > 0;
        BtnRestoreHiddenDevices.Content = count > 0
            ? string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_RestoreHidden), count)
            : LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_RestoreHiddenDevices);
    }

    private void BtnRestoreHiddenDevices_Click(object? sender, RoutedEventArgs e)
    {
        SettingsManager.ClearHiddenMultiDevices();
        _deviceListSignature = "";
        SyncMultiDeviceList();
        RefreshRestoreHiddenDevicesButton();
        if (_pods.IsConnected)
            _controller.QueryMultiDevice();
        Log.D("UI", "已清除本地隐藏设备策略并同步多设备状态");
    }

    private static string GetDeviceListRowKey(CoreMulti d)
        => string.IsNullOrEmpty(d.Address) ? (d.Name ?? d.Address) : d.Address;

    private static string GetDeviceAudioText(CoreMulti d)
        => d.IsCurrentDevice ? "  ♪" : " ♪";

    private string GetDeviceConnectionText(CoreMulti d)
    {
        var obs = d.ConnectionState switch
        {
            2 => d.IsCurrentDevice ? LanguageManager.Instance.MultiDevice_StatusCurrentDevice : LanguageManager.Instance.MultiDevice_StatusConnected,
            1 => LanguageManager.Instance.MultiDevice_StatusConnecting,
            _ => LanguageManager.Instance.MultiDevice_StatusDisconnected
        };
        return $" ({LanguageManager.Instance.GetString(obs)})";
    }

    private void UpdateDeviceListRows(IReadOnlyList<CoreMulti> all)
    {
        foreach (var d in all)
        {
            if (!_deviceListRows.TryGetValue(GetDeviceListRowKey(d), out var row))
                continue;

            row.Dot.Fill = d.ConnectionState switch { 2 => BrushGreen, 1 => BrushGray, _ => BrushRed };
            row.NameText.Text = (d.Name ?? d.Address);
            row.NameText.Foreground = d.IsCurrentDevice ? BrushLightGreen : BrushWhite;
            row.Root.Background = d.IsCurrentDevice
                ? _deviceCurrentBgBrush
                : null;
            row.AudioText.Text = GetDeviceAudioText(d);
            row.AudioText.Opacity = d.IsCurrentDevice ? 0.5 : 0.6;
            row.AudioText.Foreground = d.IsCurrentDevice ? BrushLightGreen : BrushGreen;
            row.AudioText.IsVisible = d.IsAudioActive;
            row.StatusText.Text = GetDeviceConnectionText(d);
            row.StatusText.IsVisible = d.ConnectionState != 2 && !d.IsCurrentDevice;
        }
    }

    private void UpdateDeviceListStatus(IReadOnlyList<CoreMulti> all)
    {
        // 顶部状态栏表示“当前 App 已连接的耳机型号”，不要用多设备列表里的当前主机名。
        // 多设备列表中的 current.DeviceName 可能是“xxx 的电脑/手机”，用于侧栏设备列表即可。
        _ = all;
        var caps = _modelOverride != null
            ? _controller.ForceModel(_modelOverride)
            : _pods.Caps;
        UpdateConnectionStatusText(caps);
    }
}
