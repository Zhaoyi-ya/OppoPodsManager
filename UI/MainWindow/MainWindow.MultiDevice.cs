using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia;
using MultiDeviceOperation = OppoPodsManager.Control.Core.Features.MultiDeviceOperation;
using MultiDeviceDisplayState = OppoPodsManager.Control.Core.Features.MultiDeviceDisplayState;
using BusinessSnapshot = OppoPodsManager.Control.Core.Models.BusinessSnapshot;
using ConnectedDeviceSnapshot = OppoPodsManager.Control.Core.Models.ConnectedDeviceSnapshot;

namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void CloseFloatingMenusOnBlankClick(object? sender, PointerPressedEventArgs e)
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
        SettingsView?.UpdateConnectionStrategyVisibility(connected && devices.Count > 0);
        if (!connected || manager is null)
            return;

        SettingsView?.SyncConnectionStrategy(manager, snapshot!.MultiDevice, displayState.ConnectedDevices);

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
    private void AddNextMultiDeviceMenuItem(ContextMenu menu, string resourceKey, MultiDeviceOperation operation, string address, string name)
    {
        var item = new MenuItem { Header = string.Format(resourceKey, name) };
        item.Click += (_, _) => _ = _commandDispatcher?.RunAsync("多设备操作", manager => manager.OperateMultiDeviceAsync(operation, address, CancellationToken.None));
        menu.Items.Add(item);
    }

    private void HideMultiDevice(ConnectedDeviceSnapshot device)
    {
        if (device.IsCurrent || string.IsNullOrWhiteSpace(device.Address)) return;
        if (_controlManager?.HideMultiDevice(device.Address) != true)
            return;
        SyncNextMultiDeviceList(_frontendState?.Snapshot);
        SettingsView?.RefreshRestoreHiddenDevicesButton();
        _logManager?.Debug("UI", $"本地隐藏多设备 addr={device.Address}");
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

}
