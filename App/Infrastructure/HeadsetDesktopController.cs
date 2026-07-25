using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using CoreCaps = OppoPodsManager.Core.Devices.DeviceCapabilities;
using CoreFeature = OppoPodsManager.Core.Devices.DeviceFeature;
using CoreState = OppoPodsManager.Core.Devices.HeadsetState;

namespace OppoPodsManager.Infrastructure;

/// <summary>
/// Desktop control backend. Owns connection lifecycle, device selection, discovery,
/// and feature-command policy. UI layers only bind state and forward user intents.
/// </summary>
public sealed class HeadsetDesktopController : IAsyncDisposable
{
    private readonly HeadsetUiSession _session;
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectWake = new(0, 1);
    private readonly object _devicesGate = new();
    private readonly List<(ulong Addr, string Name)> _devices = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _probeGeneration;
    private ulong _selectedAddress;
    private bool _disposed;

    public HeadsetDesktopController(HeadsetUiSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.StateChanged += OnSessionStateChanged;
    }

    public HeadsetUiSession Session => _session;

    public event Action? StateChanged;
    public event Action? DevicesChanged;

    public CoreState State => _session.State;
    public CoreCaps Caps => _session.Caps;
    public bool IsConnected => _session.IsConnected;
    public string? LastError => _session.LastError;
    public ulong SelectedAddress => Volatile.Read(ref _selectedAddress);

    public string? ModelOverride
    {
        get => _session.ModelOverride;
        set => _session.ModelOverride = value;
    }

    public IReadOnlyList<(ulong Addr, string Name)> Devices
    {
        get
        {
            lock (_devicesGate)
                return _devices.ToArray();
        }
    }

    public IReadOnlyList<string> GetModelNames() => _session.GetModelNames();

    public CoreCaps ForceModel(string modelName) => _session.ForceModel(modelName);

    public static bool FeatureOn(CoreState state, CoreFeature feature) =>
        state.FeatureStates.TryGetValue(feature, out var on) && on;

    public static bool IsStateConnected(CoreState state) =>
        state.Connection is ConnectionState.Connected or ConnectionState.Identifying;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loopTask is not null)
            return;

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunConnectionLoopAsync(_loopCts.Token));
    }

    /// <summary>
    /// Stops the connection loop and disconnects. Never hangs forever: if the loop
    /// is stuck in WinRT/RFCOMM I/O, abandons after <paramref name="timeout"/>.
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromMilliseconds(1500);

        _loopCts?.Cancel();
        // Always wake the loop so it can observe cancellation, even during dispose.
        TryReleaseReconnectWake();

        var loop = _loopTask;
        if (loop is not null)
        {
            try
            {
                var finished = await Task.WhenAny(loop, Task.Delay(budget)).ConfigureAwait(false);
                if (finished != loop)
                {
                    // Loop still stuck (e.g. RFCOMM ConnectAsync ignoring cancel). Abandon it.
                    Log.D("CTRL", $"StopAsync: connection loop did not finish within {budget.TotalMilliseconds:0}ms, abandoning");
                }
                else
                {
                    try { await loop.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        _loopTask = null;
        try { _loopCts?.Dispose(); } catch { }
        _loopCts = null;

        // Disconnect with a short budget so quit never blocks on socket cleanup.
        try
        {
            var disconnect = _session.DisconnectAsync().AsTask();
            var done = await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromMilliseconds(800)))
                .ConfigureAwait(false);
            if (done == disconnect)
            {
                try { await disconnect.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (AggregateException) { }
            }
            else
                Log.D("CTRL", "StopAsync: DisconnectAsync timed out, abandoning");
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException)
        {
            // Nested cancel/dispose from RFCOMM read loop — expected on quit.
        }
        catch (Exception ex)
        {
            Log.Ex("CTRL", "StopAsync disconnect", ex);
        }
    }

    public void SignalReconnect()
    {
        if (_disposed)
            return;
        TryReleaseReconnectWake();
    }

    private void TryReleaseReconnectWake()
    {
        try
        {
            if (_reconnectWake.CurrentCount == 0)
                _reconnectWake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        await _transportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            var generation = Interlocked.Increment(ref _probeGeneration);
            // Trust OS "currently connected" enumeration + earbud/Melody filtering done in
            // platform discovery. Do NOT full-handshake every candidate here — that path
            // opens RFCOMM/WinRT objects for every nearby device and races dispose on
            // startup (matches legacy: list first, real connect only when selected).
            var discovered = await _session.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _probeGeneration) || _disposed)
                return;

            var verified = new List<(ulong Addr, string Name)>();
            foreach (var candidate in discovered)
            {
                if (candidate.BluetoothAddress is null or 0)
                    continue;
                var address = candidate.BluetoothAddress.Value;
                var name = candidate.AdvertisedName ?? $"耳机 {address:X12}";
                verified.Add((address, name));
            }

            if (!_disposed)
                SetDevices(verified);
        }
        catch (ObjectDisposedException)
        {
            // Controller shutting down while discovery is in flight.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Discovery is best-effort; keep previous device list.
        }
        finally
        {
            try { _transportGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task SelectDeviceAsync(ulong address, string? name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _selectedAddress, address);
        await _session.ConnectSelectedAsync(address, name, cancellationToken).ConfigureAwait(false);
        SignalReconnect();
    }

    public async Task ReconnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedAddress;
        var name = Devices.FirstOrDefault(d => d.Addr == selected).Name;
        await SelectDeviceAsync(selected, name, cancellationToken).ConfigureAwait(false);
    }

    public void SetAnc(string mode) => _session.SendAnc(mode);

    public void SetSpatialAudio(string mode) => _session.SendSpatialAudio(mode);

    public void SetGameMode(bool enabled) => _session.SendGameMode(enabled);

    public void SetDualDevice(bool enabled) => _session.SendDualDevice(enabled);

    public void SetBassEngine(bool enabled) => _session.SendBassEngine(enabled);

    public void SetVocalEnhance(bool enabled) => _session.SendVocalEnhance(enabled);

    public void SetHearingEnhance(bool enabled) => _session.SendHearingEnhance(enabled);

    public void SetLongPowerMode(bool enabled) => _session.SendLongPowerMode(enabled);

    public void SetWearDetection(bool enabled) => _session.SendWearDetection(enabled);

    public void SetSpineHealth(bool enabled) => _session.SendSpineHealth(enabled);

    public void SetFindDevice(bool start) => _session.SendFindDevice(start);

    public void SetEqualizer(string name) => _session.SendEq(name);

    public void SetCustomEqualizer(IReadOnlyList<int> gains, string name) =>
        _session.SendCustomEq(gains, name);

    public void UpdateCustomEqualizer(byte id, IReadOnlyList<int> gains, string name, int min = -6, int max = 6) =>
        _session.UpdateCustomEq(id, gains, name, min, max);

    public void DeleteEqualizer(byte id) => _session.DeleteEq(id);

    public void DeleteEqualizer(int id) => _session.DeleteEq(id);

    public void QueryEqualizerDetails() => _session.SendQueryEqAll();

    public void QueryMultiDevice() => _session.SendMultiConnectInfo();

    public void MultiDeviceConnect(string address) => _session.SendMultiConnectConnect(address);

    public void MultiDeviceDisconnect(string address) => _session.SendMultiConnectDisconnect(address);

    public void MultiDeviceSetPriority(string address) => _session.SendMultiConnectSetPriority(address);

    public void MultiDeviceAutoSwitch() => _session.SendMultiConnectAutoSwitch();

    public void MultiDeviceUnpair(string address) => _session.SendMultiConnectUnpair(address);

    /// <summary>
    /// Applies spatial sound toggle and enforces game-sound mutex policy.
    /// </summary>
    public FeatureToggleResult SetSpatial(bool enabled)
    {
        var caps = Caps;
        var disabledGameSound = false;
        if (enabled && caps.GameSoundMutexSpatial)
        {
            _session.SendGameSound(false);
            disabledGameSound = true;
        }

        _session.SendSpatial(enabled);
        return new FeatureToggleResult(
            Applied: true,
            DisabledGameSound: disabledGameSound,
            DisabledSpatial: false,
            GameSoundUiEnabled: !enabled,
            SpatialUiEnabled: true,
            EqUiEnabled: !disabledGameSound);
    }

    /// <summary>
    /// Applies game-sound toggle and enforces spatial/EQ mutex policy.
    /// </summary>
    public FeatureToggleResult SetGameSound(bool enabled)
    {
        var caps = Caps;
        var disabledSpatial = false;
        if (enabled && caps.GameSoundMutexSpatial)
        {
            _session.SendSpatial(false);
            disabledSpatial = true;
        }

        _session.SendGameSound(enabled);
        return new FeatureToggleResult(
            Applied: true,
            DisabledGameSound: false,
            DisabledSpatial: disabledSpatial,
            GameSoundUiEnabled: true,
            SpatialUiEnabled: !enabled,
            EqUiEnabled: !enabled);
    }

    private async Task RunConnectionLoopAsync(CancellationToken stopToken)
    {
        await RefreshDevicesAsync(stopToken).ConfigureAwait(false);
        EnsureSelectedDevice();

        while (!stopToken.IsCancellationRequested)
        {
            var devices = Devices;
            if (devices.Count == 0)
            {
                await WaitForReconnectSignalAsync(stopToken).ConfigureAwait(false);
                await RefreshDevicesAsync(stopToken).ConfigureAwait(false);
                EnsureSelectedDevice();
                continue;
            }

            var selected = devices.FirstOrDefault(d => d.Addr == SelectedAddress);
            if (selected.Addr == 0)
                selected = devices[0];
            Volatile.Write(ref _selectedAddress, selected.Addr);

            try
            {
                await _transportGate.WaitAsync(stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (stopToken.IsCancellationRequested)
                    break;
                await _session.ConnectSelectedAsync(selected.Addr, selected.Name, stopToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                try { _transportGate.Release(); }
                catch (ObjectDisposedException) { }
            }

            if (_session.IsConnected)
            {
                // Refresh only while the connection loop is alive; pass stopToken so
                // WinRT discovery is cancelled on shutdown instead of racing dispose.
                _ = RefreshDevicesAsync(stopToken);
                try
                {
                    while (!stopToken.IsCancellationRequested && _session.IsConnected)
                        await Task.Delay(500, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (!stopToken.IsCancellationRequested)
                await WaitForReconnectSignalAsync(stopToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForReconnectSignalAsync(CancellationToken stopToken)
    {
#if WINDOWS
        await _reconnectWake.WaitAsync(stopToken).ConfigureAwait(false);
#else
        try
        {
            await _reconnectWake.WaitAsync(TimeSpan.FromSeconds(5), stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            throw;
        }

        while (_reconnectWake.CurrentCount > 0)
            await _reconnectWake.WaitAsync(stopToken).ConfigureAwait(false);
#endif
    }

    private void EnsureSelectedDevice()
    {
        var devices = Devices;
        if (devices.Count == 0)
        {
            Volatile.Write(ref _selectedAddress, 0UL);
            return;
        }

        if (devices.All(d => d.Addr != SelectedAddress))
            Volatile.Write(ref _selectedAddress, devices[0].Addr);
    }

    private void SetDevices(IReadOnlyList<(ulong Addr, string Name)> devices)
    {
        lock (_devicesGate)
        {
            _devices.Clear();
            _devices.AddRange(devices);
        }

        DevicesChanged?.Invoke();
    }

    private void OnSessionStateChanged() => StateChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // Stop the loop first (needs wake semaphore still alive), then mark disposed.
        _session.StateChanged -= OnSessionStateChanged;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;

        try { _transportGate.Dispose(); } catch { }
        try { _reconnectWake.Dispose(); } catch { }
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>Result of a feature toggle that may force related UI/state changes.</summary>
public readonly record struct FeatureToggleResult(
    bool Applied,
    bool DisabledGameSound,
    bool DisabledSpatial,
    bool GameSoundUiEnabled,
    bool SpatialUiEnabled,
    bool EqUiEnabled);
