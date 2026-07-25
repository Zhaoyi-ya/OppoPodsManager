using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Core.Connections;

public sealed record RawDeviceCandidate(
    string StableId,
    string? PlatformDeviceId,
    ulong? BluetoothAddress,
    string? AdvertisedName,
    IReadOnlySet<Guid> ServiceUuids,
    IReadOnlySet<DeviceTransport> AvailableTransports);
