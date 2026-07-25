using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Application.State;

/// <summary>
/// Read-only application-facing view of the current brand session.
/// The UI consumes this projection instead of retaining a mutable session object.
/// </summary>
public sealed record HeadsetStateSnapshot(
    DeviceIdentity? Identity,
    DeviceCapabilities? Capabilities,
    HeadsetState? State)
{
    public bool IsConnected => State?.Connection == ConnectionState.Connected;

    public static HeadsetStateSnapshot Empty { get; } = new(null, null, null);

    public static HeadsetStateSnapshot From(IBrandSession? session) =>
        session is null
            ? Empty
            : new(session.Identity, session.Capabilities, session.State);
}
