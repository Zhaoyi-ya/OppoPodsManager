using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Core.Connections;

public sealed record ConnectionProfile(
    DeviceTransport Transport,
    string Name,
    int Priority = 0);
