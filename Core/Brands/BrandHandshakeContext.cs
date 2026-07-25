using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Core.Brands;

public sealed record BrandHandshakeContext(
    RawDeviceCandidate Device,
    TimeSpan ProbeTimeout,
    TimeSpan TotalTimeout);
