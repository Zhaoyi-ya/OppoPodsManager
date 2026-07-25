using OppoPodsManager.Application.Connection;
using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Core.Results;
using OppoPodsManager.Application.State;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Application.Discovery;

namespace OppoPodsManager.Application.Control;

/// <summary>
/// Generic feature entry point. Brand/protocol details stay behind IBrandSession.
/// </summary>
public sealed class HeadsetControlService
{
    private readonly HeadsetSessionCoordinator _sessions;
    private readonly DeviceDiscoveryService _discovery;

    public HeadsetControlService(
        HeadsetSessionCoordinator sessions,
        DeviceDiscoveryService discovery)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public IBrandSession? CurrentSession => _sessions.CurrentSession;

    public HeadsetState? CurrentState => _sessions.CurrentSession?.State;

    public DeviceCapabilities? CurrentCapabilities => _sessions.CurrentSession?.Capabilities;

    public HeadsetStateSnapshot CurrentSnapshot => _sessions.CurrentSnapshot;

    public event Action? StateChanged
    {
        add => _sessions.SessionChanged += value;
        remove => _sessions.SessionChanged -= value;
    }

    public bool Supports(DeviceFeature feature) =>
        CurrentCapabilities?.Supports(feature) == true;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken) =>
        await _sessions.ConnectAsync(cancellationToken);

    public async ValueTask<BrandConnectionResult?> ConnectDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken = default) =>
        await _sessions.ConnectDeviceAsync(device, cancellationToken);

    public ValueTask<bool> ProbeDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken = default) =>
        _sessions.ProbeDeviceAsync(device, cancellationToken);

    public async ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
    {
        await _sessions.DisconnectAsync();
        await _sessions.ConnectAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(
        CancellationToken cancellationToken = default) =>
        _discovery.DiscoverAsync(cancellationToken);

    public async ValueTask DisconnectAsync() =>
        await _sessions.DisconnectAsync();

    public ValueTask<OperationResult> ExecuteAsync(
        DeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        var session = CurrentSession;
        return session is null
            ? ValueTask.FromResult(OperationResult.Failure(CommandFailure.NotConnected()))
            : session.ExecuteAsync(command, cancellationToken);
    }

    public ValueTask<OperationResult> SetAncAsync(string mode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetAnc(mode), cancellationToken);

    public ValueTask<OperationResult> SetSpatialAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetSpatial(enabled), cancellationToken);

    public ValueTask<OperationResult> SetSpatialAudioAsync(string mode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetSpatialAudio(mode), cancellationToken);

    public ValueTask<OperationResult> SetGameModeAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetGameMode(enabled), cancellationToken);

    public ValueTask<OperationResult> SetGameSoundAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetGameSound(enabled), cancellationToken);

    public ValueTask<OperationResult> SetFeatureAsync(
        DeviceFeature feature,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetFeature(feature, enabled), cancellationToken);

    public ValueTask<OperationResult> SetEqualizerAsync(string name, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.SetEqualizer(name), cancellationToken);

    public ValueTask<OperationResult> SetCustomEqualizerAsync(
        IReadOnlyList<int> gains,
        string name,
        byte? id = null,
        IReadOnlyList<int>? frequencies = null,
        int minimum = -6,
        int maximum = 6,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            new DeviceCommand.SetCustomEqualizer(gains, name, id, frequencies, minimum, maximum),
            cancellationToken);

    public ValueTask<OperationResult> DeleteEqualizerAsync(
        byte id,
        EqualizerEntry? entry = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.DeleteEqualizer(id, entry), cancellationToken);

    public ValueTask<OperationResult> QueryBatteryAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.QueryBattery(), cancellationToken);

    public ValueTask<OperationResult> QueryEqualizerAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.QueryEqualizer(), cancellationToken);

    public ValueTask<OperationResult> QueryEqualizerDetailsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.QueryEqualizerDetails(), cancellationToken);

    public ValueTask<OperationResult> QueryMultiDeviceAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.QueryMultiDevice(), cancellationToken);

    public ValueTask<OperationResult> FindDeviceAsync(bool start, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.FindDevice(start), cancellationToken);

    public ValueTask<OperationResult> OperateMultiDeviceAsync(
        string address,
        MultiDeviceOperation operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new DeviceCommand.OperateMultiDevice(address, operation), cancellationToken);

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();
}
