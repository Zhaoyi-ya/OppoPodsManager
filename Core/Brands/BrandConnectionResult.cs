namespace OppoPodsManager.Core.Brands;

public sealed record BrandConnectionResult(
    IBrandSession Session,
    BrandHandshakeResult Handshake);
