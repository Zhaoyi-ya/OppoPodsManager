using OppoPodsManager.Core.Communication;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Melody NotificationCommandManager 的桌面端实现：先读取设备通知能力，
/// 再按设备能力选择批量注册(0x0205)或逐事件注册(0x0201)。
/// 通知只负责实时状态；保底查询由 ShouldPoll 返回的计划决定。
/// </summary>
public sealed class OppoNotificationCoordinator
{
    public const ushort QueryCapabilityCommand = 0x0200;
    public const ushort RegisterSingleCommand = 0x0201;
    public const ushort RegisterMultiCommand = 0x0205;

    public const ushort RegisterSingleResponse = 0x8201;
    public const ushort RegisteredEvent = 0x8202;
    /// <summary>Unsolicited notification event push (Melody handles alongside 0x8202).</summary>
    public const ushort PushNotifyEvent = 0x0204;
    public const ushort RegisterMultiResponse = 0x8205;

    public const byte BatteryEvent = 0x01;
    public const byte WearingEvent = 0x02;
    public const byte NoiseModeEvent = 0x03;
    public const byte GameModeEvent = 0x05;
    public const byte MultiDeviceEvent = 0x06;

    private static readonly byte[] DesiredEvents =
    [BatteryEvent, WearingEvent, NoiseModeEvent, GameModeEvent, MultiDeviceEvent];

    private readonly TimeProvider _time;
    private readonly Dictionary<byte, DateTimeOffset> _lastEvents = new();
    private HashSet<byte> _supportedEvents = [];

    public OppoNotificationCoordinator(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public bool CapabilityQuerySent { get; private set; }
    public bool RegistrationAcknowledged { get; private set; }
    public IReadOnlySet<byte> SupportedEvents => _supportedEvents;

    public void Reset()
    {
        CapabilityQuerySent = false;
        RegistrationAcknowledged = false;
        _supportedEvents = [];
        _lastEvents.Clear();
    }

    public NotificationCommand CreateCapabilityQuery()
    {
        CapabilityQuerySent = true;
        return new NotificationCommand(QueryCapabilityCommand, []);
    }

    /// <summary>严格校验 [status][count][event...]，拒绝截断响应。</summary>
    public IReadOnlyList<NotificationCommand> HandleCapabilityResponse(
        ReadOnlySpan<byte> payload,
        bool supportsBatchRegistration)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return [];

        var count = payload[1];
        if (payload.Length < count + 2)
            return [];

        var supported = new HashSet<byte>();
        for (var index = 0; index < count; index++)
            supported.Add(payload[index + 2]);
        _supportedEvents = supported;
        var desired = DesiredEvents.Where(_supportedEvents.Contains).ToArray();
        if (desired.Length == 0)
            return [];

        if (supportsBatchRegistration)
            return [new NotificationCommand(
                RegisterMultiCommand,
                BuildBatchRegistrationPayload(desired))];

        return desired
            .Select(eventId => new NotificationCommand(
                RegisterSingleCommand,
                [eventId]))
            .ToArray();
    }

    public void HandleRegistrationResponse(ushort command, ReadOnlySpan<byte> payload)
    {
        if (command is not RegisterSingleResponse and not RegisterMultiResponse)
            return;
        if (payload.Length > 0 && payload[0] == 0)
            RegistrationAcknowledged = true;
    }

    /// <summary>
    /// 解析通知事件载荷。
    /// <list type="bullet">
    /// <item><c>0x8202</c> 注册后事件：<c>[status][eventId][data...]</c>（status 须为 0）</item>
    /// <item><c>0x0204</c> 主动推送：<c>[eventId][data...]</c>（无 status 前缀）</item>
    /// </list>
    /// 与 legacy <c>PodManager.ParseActiveReport</c> / Melody <c>NotificationCommandManager.b</c> 对齐。
    /// 不强制事件必须出现在 0x8200 能力集中——设备可能推送我们已注册或关心的事件。
    /// </summary>
    public bool TryParseEvent(
        ReadOnlySpan<byte> payload,
        bool hasStatusPrefix,
        out byte eventId,
        out ReadOnlySpan<byte> eventData)
    {
        eventId = 0;
        eventData = default;

        if (hasStatusPrefix)
        {
            // 0x8202: [status][eventId][data...]
            if (payload.Length < 2 || payload[0] != 0)
                return false;
            eventId = payload[1];
            eventData = payload.Length > 2 ? payload[2..] : ReadOnlySpan<byte>.Empty;
        }
        else
        {
            // 0x0204: [eventId][data...]
            if (payload.Length < 1)
                return false;
            eventId = payload[0];
            eventData = payload.Length > 1 ? payload[1..] : ReadOnlySpan<byte>.Empty;
        }

        // Track freshness for poll suppression. Accept desired events even if the
        // capability set is empty/stale so live pushes still update UI.
        if (_supportedEvents.Count == 0 || _supportedEvents.Contains(eventId)
            || DesiredEvents.Contains(eventId))
        {
            _lastEvents[eventId] = _time.GetUtcNow();
            return true;
        }

        return false;
    }

    /// <summary>兼容旧调用：按 0x8202（带 status）解析。</summary>
    public bool HandleRegisteredEvent(ReadOnlySpan<byte> payload, out byte eventId) =>
        TryParseEvent(payload, hasStatusPrefix: true, out eventId, out _);

    /// <summary>
    /// 通知正常时跳过对应查询；超过保底窗口则恢复查询。
    /// </summary>
    public bool ShouldPoll(byte eventId, TimeSpan fallbackAfter)
    {
        if (!_supportedEvents.Contains(eventId) || !RegistrationAcknowledged)
            return true;

        return !_lastEvents.TryGetValue(eventId, out var last)
            || _time.GetUtcNow() - last >= fallbackAfter;
    }

    public static byte[] BuildBatchRegistrationPayload(IEnumerable<byte> eventIds)
    {
        var ids = eventIds.Distinct().Take(byte.MaxValue).ToArray();
        return [(byte)ids.Length, .. ids];
    }
}

public readonly record struct NotificationCommand(ushort Command, byte[] Payload);
