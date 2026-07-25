using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Application.Control;
using OppoPodsManager.Brands.Oppo;
using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Core.Results;
using CoreCaps = OppoPodsManager.Core.Devices.DeviceCapabilities;
using CoreState = OppoPodsManager.Core.Devices.HeadsetState;
using CoreFeature = OppoPodsManager.Core.Devices.DeviceFeature;

namespace OppoPodsManager.Infrastructure;

/// <summary>
/// UI-facing session facade over HeadsetControlService.
/// Replaces the legacy IPodManager / PodManagerSession surface.
/// </summary>
public sealed class HeadsetUiSession : IAsyncDisposable
{
    private readonly HeadsetControlService _control;
    private readonly OppoProfileLoader _profiles = new();
    private string? _modelOverride;
    private CoreCaps? _overrideCaps;

    public HeadsetUiSession(HeadsetControlService control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _control.StateChanged += OnControlStateChanged;
    }

    public event Action? StateChanged;

    public HeadsetControlService Control => _control;

    public CoreState State => _control.CurrentState ?? new CoreState();

    public CoreCaps Caps
    {
        get
        {
            if (_overrideCaps is not null)
                return MergeOverride(_control.CurrentCapabilities, _overrideCaps);
            return _control.CurrentCapabilities
                   ?? new CoreCaps(new HashSet<CoreFeature>())
                   {
                       IsSupported = false,
                       ModelName = "Unknown",
                   };
        }
    }

    public bool IsConnected =>
        _control.CurrentState?.Connection is ConnectionState.Connected or ConnectionState.Identifying;

    public string? LastError { get; private set; }

    public string? ModelOverride
    {
        get => _modelOverride;
        set
        {
            _modelOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            _overrideCaps = _modelOverride is null ? null : _profiles.ForceModel(_modelOverride);
            StateChanged?.Invoke();
        }
    }

    public IReadOnlyList<string> GetModelNames() => _profiles.GetModelNames();

    public CoreCaps ForceModel(string modelName) => _profiles.ForceModel(modelName);

    public async ValueTask ConnectSelectedAsync(ulong address, string? name, CancellationToken ct = default)
    {
        LastError = null;
        if (address == 0)
        {
            await _control.ConnectAsync(ct);
            return;
        }

        var device = ToCandidate(address, name);
        var result = await _control.ConnectDeviceAsync(device, ct);
        if (result is null)
            LastError = "connect failed";
    }

    public ValueTask DisconnectAsync() => _control.DisconnectAsync();

    public ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(CancellationToken ct = default) =>
        _control.DiscoverAsync(ct);

    public ValueTask<bool> ProbeAsync(ulong address, string? name, CancellationToken ct = default) =>
        _control.ProbeDeviceAsync(ToCandidate(address, name), ct);

    public void Fire(Func<CancellationToken, ValueTask<OperationResult>> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await action(CancellationToken.None);
                if (!result.Succeeded)
                    LastError = result.Error ?? "command failed";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        });
    }

    public void SendAnc(string mode) => Fire(ct => _control.SetAncAsync(mode, ct));
    public void SendSpatial(bool on) => Fire(ct => _control.SetSpatialAsync(on, ct));
    public void SendSpatialAudio(string mode) => Fire(ct => _control.SetSpatialAudioAsync(mode, ct));
    public void SendGameMode(bool on) => Fire(ct => _control.SetGameModeAsync(on, ct));
    public void SendGameSound(bool on) => Fire(ct => _control.SetGameSoundAsync(on, ct));
    public void SendEq(string name) => Fire(ct => _control.SetEqualizerAsync(name, ct));
    public void SendCustomEq(IReadOnlyList<int> gains, string name) =>
        Fire(ct => _control.SetCustomEqualizerAsync(gains, name, frequencies: Caps.CustomEqFrequencies, cancellationToken: ct));
    public void UpdateCustomEq(byte id, IReadOnlyList<int> gains, string name, int min = -6, int max = 6) =>
        Fire(ct => _control.SetCustomEqualizerAsync(gains, name, id, Caps.CustomEqFrequencies, min, max, ct));
    public void DeleteEq(byte id) => Fire(ct => _control.DeleteEqualizerAsync(id, cancellationToken: ct));
    public void DeleteEq(int id) => DeleteEq((byte)id);
    public void SendQueryEqAll() => Fire(ct => _control.QueryEqualizerDetailsAsync(ct));
    public void SendBattery() => Fire(ct => _control.QueryBatteryAsync(ct));
    public void SendFindDevice(bool start) => Fire(ct => _control.FindDeviceAsync(start, ct));
    public void SendDualDevice(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.DualDevice, on, ct));
    public void SendBassEngine(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.BassEngine, on, ct));
    public void SendVocalEnhance(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.VocalEnhance, on, ct));
    public void SendHearingEnhance(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.HearingEnhance, on, ct));
    public void SendLongPowerMode(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.LongPowerMode, on, ct));
    public void SendWearDetection(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.WearDetection, on, ct));
    public void SendSpineHealth(bool on) => Fire(ct => _control.SetFeatureAsync(CoreFeature.SpineHealth, on, ct));
    public void SendMultiConnectInfo() => Fire(ct => _control.QueryMultiDeviceAsync(ct));
    public void SendMultiConnectConnect(string address) =>
        Fire(ct => _control.OperateMultiDeviceAsync(address, MultiDeviceOperation.Connect, ct));
    public void SendMultiConnectDisconnect(string address) =>
        Fire(ct => _control.OperateMultiDeviceAsync(address, MultiDeviceOperation.Disconnect, ct));
    public void SendMultiConnectSetPriority(string address) =>
        Fire(ct => _control.OperateMultiDeviceAsync(address, MultiDeviceOperation.SetPriority, ct));
    public void SendMultiConnectAutoSwitch() =>
        Fire(ct => _control.OperateMultiDeviceAsync(string.Empty, MultiDeviceOperation.AutoSwitch, ct));
    public void SendMultiConnectUnpair(string address) =>
        Fire(ct => _control.OperateMultiDeviceAsync(address, MultiDeviceOperation.Unpair, ct));
    public void SendOperateHandheld(string address, bool connect = true) =>
        Fire(ct => _control.OperateMultiDeviceAsync(
            address,
            connect ? MultiDeviceOperation.Connect : MultiDeviceOperation.Disconnect,
            ct));

    private void OnControlStateChanged() => StateChanged?.Invoke();

    private static RawDeviceCandidate ToCandidate(ulong address, string? name) =>
        new(
            StableId: address.ToString("X12"),
            PlatformDeviceId: null,
            BluetoothAddress: address,
            AdvertisedName: name,
            ServiceUuids: new HashSet<Guid>(),
            AvailableTransports: new HashSet<DeviceTransport>
            {
                DeviceTransport.Rfcomm,
                DeviceTransport.BluetoothClassic,
                DeviceTransport.Gatt,
            });

    private static CoreCaps MergeOverride(CoreCaps? live, CoreCaps overrideCaps)
    {
        if (live is null)
            return overrideCaps;
        return overrideCaps with
        {
            HasSpatialAudio = live.HasSpatialAudio || overrideCaps.HasSpatialAudio,
            HasSpatialSound = live.HasSpatialSound || overrideCaps.HasSpatialSound,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _control.StateChanged -= OnControlStateChanged;
        await _control.DisposeAsync();
    }
}
