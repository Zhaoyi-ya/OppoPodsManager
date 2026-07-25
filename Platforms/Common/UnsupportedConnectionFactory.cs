using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Platforms.Common;

public sealed class UnsupportedConnectionFactory : IPlatformConnectionFactory
{
    public IReadOnlyList<ConnectionProfile> GetProfiles(RawDeviceCandidate device) => [];

    public ValueTask<IRawConnection> OpenAsync(
        RawDeviceCandidate device,
        ConnectionProfile profile,
        CancellationToken cancellationToken)
        => throw new PlatformNotSupportedException(
            "当前平台尚未注册耳机原始连接实现。");
}
