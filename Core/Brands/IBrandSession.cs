using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Core.Results;

namespace OppoPodsManager.Core.Brands;

public interface IBrandSession : IAsyncDisposable
{
    DeviceIdentity Identity { get; }

    DeviceCapabilities Capabilities { get; }

    HeadsetState State { get; }

    IRawConnection Connection { get; }

    event Action? StateChanged;

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<OperationResult> ExecuteAsync(
        DeviceCommand command,
        CancellationToken cancellationToken);
}
