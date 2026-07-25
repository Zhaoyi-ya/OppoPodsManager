using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Core.Brands;

public enum BrandHandshakeStatus
{
    Matched,
    NotMatched,
    Inconclusive,
    Failed,
}

public sealed record BrandHandshakeResult(
    BrandHandshakeStatus Status,
    DeviceIdentity? Identity = null,
    DeviceCapabilities? Capabilities = null,
    string? ProtocolVersion = null,
    string? Error = null)
{
    public bool IsMatched => Status == BrandHandshakeStatus.Matched;
}
