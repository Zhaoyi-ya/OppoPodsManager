using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

public sealed record OppoPollRequest(
    ushort Command,
    byte[] Payload,
    TimeSpan Interval,
    DeviceFeature Feature,
    IReadOnlySet<ushort> RequiredCommands,
    byte? NotificationEvent = null);

/// <summary>
/// Explicit fallback-query plan. Notification delivery is preferred; this plan
/// only runs when the corresponding event is unavailable or stale.
/// </summary>
public sealed class OppoPollPlan
{
    private readonly IReadOnlyList<OppoPollRequest> _requests;

    private OppoPollPlan(IReadOnlyList<OppoPollRequest> requests)
    {
        _requests = requests;
    }

    public IReadOnlyList<OppoPollRequest> Requests => _requests;

    public static OppoPollPlan Build(
        IReadOnlySet<DeviceFeature> effectiveFeatures,
        IReadOnlySet<ushort> supportedCommands)
    {
        var requests = new List<OppoPollRequest>();

        Add(requests, effectiveFeatures, supportedCommands, DeviceFeature.Battery,
            OppoCommandIds.QueryBattery, [], TimeSpan.FromSeconds(10),
            OppoNotificationCoordinator.BatteryEvent);

        Add(requests, effectiveFeatures, supportedCommands, DeviceFeature.Anc,
            OppoCommandIds.QueryAnc, [0x01, 0x01], TimeSpan.FromSeconds(8),
            OppoNotificationCoordinator.NoiseModeEvent);

        Add(requests, effectiveFeatures, supportedCommands, DeviceFeature.Equalizer,
            OppoCommandIds.QueryEq, [], TimeSpan.FromMinutes(2));

        Add(requests, effectiveFeatures, supportedCommands, DeviceFeature.Equalizer,
            OppoCommandIds.QueryEqualizerDetails, [0x01, 0x05], TimeSpan.FromMinutes(2));

        Add(requests, effectiveFeatures, supportedCommands, DeviceFeature.MultiDevice,
            OppoCommandIds.QueryMultiDevice, [], TimeSpan.FromSeconds(30),
            OppoNotificationCoordinator.MultiDeviceEvent);

        // Dual-device priority strategy (0x0132). Not gated by supportedCommands —
        // some firmwares omit it from 0x0100 but still answer.
        if (effectiveFeatures.Contains(DeviceFeature.MultiDevice)
            || effectiveFeatures.Contains(DeviceFeature.DualDevice))
        {
            requests.Add(new OppoPollRequest(
                OppoCommandIds.QueryMultiPriority,
                [],
                TimeSpan.FromSeconds(30),
                DeviceFeature.MultiDevice,
                new HashSet<ushort> { OppoCommandIds.QueryMultiPriority },
                OppoNotificationCoordinator.MultiDeviceEvent));
        }

        // Feature switches: poll 0x010D with a non-empty feature list (empty payload is invalid).
        if (supportedCommands.Contains(OppoCommandIds.QueryFeatureState))
        {
            requests.Add(new OppoPollRequest(
                OppoCommandIds.QueryFeatureState,
                OppoSession.BuildFeatureQueryPayload(effectiveFeatures),
                TimeSpan.FromSeconds(15),
                DeviceFeature.Gaming,
                new HashSet<ushort> { OppoCommandIds.QueryFeatureState }));
        }

        return new OppoPollPlan(requests);
    }

    private static void Add(
        List<OppoPollRequest> requests,
        IReadOnlySet<DeviceFeature> features,
        IReadOnlySet<ushort> commands,
        DeviceFeature feature,
        ushort command,
        byte[] payload,
        TimeSpan interval,
        byte? notificationEvent = null)
    {
        if (!features.Contains(feature) || !commands.Contains(command))
            return;

        requests.Add(new OppoPollRequest(
            command,
            payload,
            interval,
            feature,
            new HashSet<ushort> { command },
            notificationEvent));
    }
}
