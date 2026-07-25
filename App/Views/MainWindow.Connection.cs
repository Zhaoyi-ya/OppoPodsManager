using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Infrastructure;
using OppoPodsManager.Localization;

namespace OppoPodsManager;

public partial class MainWindow
{
    private void StartDesktopControl()
    {
        _controller.DevicesChanged += OnControllerDevicesChanged;
        _controller.StateChanged += OnStateChanged;
        _controller.Start();
        SyncDevicePickerFromController();
    }

    private void OnControllerDevicesChanged()
    {
        Dispatcher.UIThread.Post(SyncDevicePickerFromController);
    }

    private void SyncDevicePickerFromController()
    {
        var devices = _controller.Devices
            .Select(d => (addr: d.Addr, name: d.Name))
            .ToList();
        ApplyEarbudDevices(devices);
        _selectedEarbudAddress = _controller.SelectedAddress;
    }

    private async void RefreshDevices_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 刷新多耳机列表");
        try { await _controller.RefreshDevicesAsync(); }
        catch (Exception ex) { Log.Ex("UI", "RefreshConnectedEarbuds", ex); }
    }

    private void ApplyEarbudDevices(IReadOnlyList<(ulong addr, string name)> devices)
    {
        _earbudDevices.Clear();
        _earbudDevices.AddRange(devices);
        _suppressEarbudSelection = true;
        CbDevice.Items.Clear();
        foreach (var (_, name) in _earbudDevices)
            CbDevice.Items.Add(name);

        var selected = _earbudDevices.FindIndex(device => device.addr == _selectedEarbudAddress);
        if (selected < 0 && _earbudDevices.Count > 0)
        {
            selected = _earbudDevices.FindIndex(device => device.addr == _controller.SelectedAddress);
            if (selected < 0) selected = 0;
            _selectedEarbudAddress = _earbudDevices[selected].addr;
        }

        if (selected >= 0)
            CbDevice.SelectedIndex = selected;
        _suppressEarbudSelection = false;
        CbDevice.IsVisible = _earbudDevices.Count >= 1;
        BtnRefreshDevices.IsVisible = _earbudDevices.Count >= 1;
    }

    private async void CbDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEarbudSelection || CbDevice.SelectedIndex < 0 || CbDevice.SelectedIndex >= _earbudDevices.Count)
            return;

        var (address, name) = _earbudDevices[CbDevice.SelectedIndex];
        if (address == _selectedEarbudAddress)
            return;

        Log.D("UI", $"用户操作: 切换多耳机 -> addr={address:X12} name=\"{name}\"");
        _selectedEarbudAddress = address;
        ResetUi();
        try { await _controller.SelectDeviceAsync(address, name); }
        catch (Exception ex) { Log.Ex("UI", "SelectDevice", ex); }
    }

#if WINDOWS
    private void OnBluetoothDevicesChanged(IReadOnlyList<RawDeviceCandidate> devices)
    {
        _ = devices;
        if (_realClose) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_realClose || _bluetoothChangeScheduled) return;
            _bluetoothChangeScheduled = true;
            _ = HandleBluetoothDevicesChangedAsync();
        });
    }

    private async Task HandleBluetoothDevicesChangedAsync()
    {
        try
        {
            await _controller.RefreshDevicesAsync();
            if (_realClose) return;

            var selectedStillConnected = _controller.Devices.Any(d => d.Addr == _controller.SelectedAddress);
            if (!selectedStillConnected)
            {
                ResetUi();
                _controller.SignalReconnect();
            }
            else
            {
                _controller.SignalReconnect();
            }
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "BluetoothDevicesChanged", ex);
        }
        finally
        {
            _bluetoothChangeScheduled = false;
        }
    }
#endif

    private async void Reconnect_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 点击重连");
        try { await _controller.ReconnectSelectedAsync(); }
        catch (Exception ex) { Log.Ex("UI", "Reconnect", ex); }
    }

    private void ResetUi()
    {
        SetBatLabel(LeftLabel, LeftChargeBolt, LeftBatteryProgress, null);
        SetBatLabel(RightLabel, RightChargeBolt, RightBatteryProgress, null);
        SetBatLabel(CaseLabel, CaseChargeBolt, CaseBatteryProgress, null);
        WearStatus.Text = "";
        DeviceList.Items.Clear();
        _deviceListRows.Clear();
        _deviceListSignature = "";
        _findDeviceActive = false;
        BtnFindDevice.Content = _findDevice;
        BtnFindDevice.IsEnabled = false;
        AncSubRow.IsVisible = false;
        // 断开后清空回读缓存；无设备信息时显示关闭（不使用三态中间态）。
        _prevSpatialSound = _prevGameMode = _prevGameSound = _prevDualDevice = false;
        _prevBassEngine = _prevVocalEnhance = _prevHearingEnhance = false;
        _prevLongPowerMode = _prevWearDetection = _prevSpineHealth = false;
        _prevSpatialMode = null;
        SetCheckSilent(CbSpatial, CbSpatial_Changed, false);
        SetCheckSilent(CbGame, CbGame_Changed, false);
        SetCheckSilent(CbGameSound, CbGameSound_Changed, false);
        SetCheckSilent(CbDualDevice, CbDualDevice_Changed, false);
        SetCheckSilent(CbBassEngine, CbBassEngine_Changed, false);
        SetCheckSilent(CbVocalEnhance, CbVocalEnhance_Changed, false);
        SetCheckSilent(CbHearingEnhance, CbHearingEnhance_Changed, false);
        SetCheckSilent(CbLongPower, CbLongPower_Changed, false);
        SetCheckSilent(CbWearDetection, CbWearDetection_Changed, false);
        SetCheckSilent(CbSpineHealth, CbSpineHealth_Changed, false);
        CbSpatial.IsEnabled = true;
        CbGameSound.IsEnabled = true;
        SetEqControlsEnabled(true);
    }

    private void OnWindowClosing(object? s, WindowClosingEventArgs e)
    {
        if (_realClose) return;

        if (SettingsManager.GetBool("TrayEnabled", false))
        {
            e.Cancel = true;
            _logRefreshTimer?.Stop();
            ShowInTaskbar = false;
            Hide();
            return;
        }

        e.Cancel = true;
        QuitApplication();
    }

    /// <summary>统一退出：停止后端控制、释放 UI 资源、终止进程。</summary>
    private void QuitApplication()
    {
        if (_realClose) return;
        _realClose = true;
        Closing -= OnWindowClosing;
        DisposeRuntimeUiResources();
        if (_trayIcon != null) _trayIcon.IsVisible = false;

        // Never block the UI thread on Stop/Dispose — that deadlocks when the
        // connection loop posts back to the dispatcher or holds WinRT I/O.
        // StopAsync has its own timeout; we only wait briefly then force-exit.
        try
        {
            var stop = Task.Run(async () =>
            {
                try { await _controller.StopAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Log.Ex("UI", "QuitApplication StopAsync", ex); }
                try { await _controller.DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Log.Ex("UI", "QuitApplication DisposeAsync", ex); }
            });

            if (!stop.Wait(TimeSpan.FromMilliseconds(2000)))
                Log.D("UI", "QuitApplication: controller shutdown timed out, forcing exit");
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(static e =>
            e is OperationCanceledException or ObjectDisposedException or TaskCanceledException))
        {
            // Expected when cancel races dispose during quit.
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "QuitApplication", ex);
        }

        // Clear static so App.OnShutdownRequested does not dispose twice.
        App.DesktopController = null;
        App.HeadsetControl = null;
        Environment.Exit(0);
    }

    private void DisposeRuntimeUiResources()
    {
        if (_runtimeUiDisposed)
            return;
        _runtimeUiDisposed = true;

        Closing -= OnWindowClosing;
        _eqDebounceTimer?.Stop();
        _bgApplyDebounceTimer?.Stop();
        _logRefreshTimer?.Stop();
        Trace.Listeners.Remove(_logTraceListener);
        _logManager.Dispose();
        _trayClickTimer?.Stop();
        if (_trayIcon != null)
        {
            _trayIcon.Clicked -= OnTrayClicked;
            _trayIcon.Menu = null;
        }
        _trayAncMap.Clear();
        SetBackgroundImageSource(null, "");
        ClearBackgroundBitmapCache(keepKey: null);
        ClearBackgroundThumbCache();
        _smallWindow?.Close();
    }
}
