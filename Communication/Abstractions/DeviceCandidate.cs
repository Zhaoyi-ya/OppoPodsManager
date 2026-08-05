namespace OppoPodsManager.Communication.Abstractions;

public sealed record DeviceCandidate(
    string StableId,
    string PlatformId,
    string? BluetoothAddress,
    string DisplayName,
    string Brand,
    IReadOnlyCollection<Guid> ServiceIds,
    IReadOnlyCollection<string> Transports);
