namespace OppoPodsManager.Core.Devices;

public sealed record DeviceIdentity(
    string StableId,
    DeviceBrand Brand,
    string? ModelId,
    string? DisplayName,
    ulong? BluetoothAddress,
    string? PlatformDeviceId);
