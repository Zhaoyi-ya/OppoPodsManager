using System.Collections.Concurrent;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Oppo.Managers;

// 管理设备通知能力查询、注册和事件的新鲜度记录。
public sealed class Notifier : IDisposable
{
    public const byte BatteryEvent = 0x01;
    public const byte WearEvent = 0x02;
    public const byte NoiseCancellationEvent = 0x03;
    public const byte GameModeEvent = 0x05;
    public const byte MultiDeviceEvent = 0x06;

    private static readonly byte[] InterestedEvents = [
        BatteryEvent,
        WearEvent,
        NoiseCancellationEvent,
        GameModeEvent,
        MultiDeviceEvent
    ];

    private readonly ICommandRequester _channel;
    private readonly FrameRouter _router;
    private readonly bool _supportsBatchRegistration;
    private readonly ConcurrentDictionary<byte, DateTimeOffset> _lastEvents = new();
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public Notifier(ICommandRequester channel, FrameRouter router, bool supportsBatchRegistration)
    {
        _channel = channel;
        _router = router;
        _supportsBatchRegistration = supportsBatchRegistration;
        _subscriptions.Add(router.Subscribe(CommandId.NotificationEventResponse, ReceiveNotification));
        _subscriptions.Add(router.Subscribe(CommandId.NotificationEvent, ReceiveNotification));
        _subscriptions.Add(router.Subscribe(CommandId.EqualizerChangedNotification, ReceiveEqualizerChanged));
    }

    public event EventHandler<NotificationReceived>? NotificationReceived;
    public event EventHandler<EqualizerChangedReceived>? EqualizerChanged;

    public IReadOnlySet<byte> RegisteredEvents { get; private set; } = new HashSet<byte>();

    public bool HasFreshEvent(byte eventId, TimeSpan freshness, DateTimeOffset now)
        => _lastEvents.TryGetValue(eventId, out var lastReceived) && now - lastReceived < freshness;

    // 优先尝试批量注册，不支持时自动降级为逐事件注册。
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Notifier", $"开始通知初始化：batch={_supportsBatchRegistration}。");
        var capabilities = await RequestAsync(
            CommandId.NotificationCapabilities,
            CommandId.NotificationCapabilitiesResponse,
            Array.Empty<byte>(),
            cancellationToken);

        var supportedEvents = ParseCapabilities(capabilities.Payload.Span);
        var requestedEvents = InterestedEvents.Where(supportedEvents.Contains).ToArray();
        ApplicationLog.Current?.Debug("Notifier", $"通知能力：supported=[{string.Join(",", supportedEvents)}]，requested=[{string.Join(",", requestedEvents)}]。");
        if (requestedEvents.Length == 0)
        {
            ApplicationLog.Current?.Info("Notifier", "设备没有可注册的目标通知事件。");
            return;
        }

        if (_supportsBatchRegistration && await TryRegisterBatchAsync(requestedEvents, cancellationToken))
        {
            RegisteredEvents = new HashSet<byte>(requestedEvents);
            ApplicationLog.Current?.Info("Notifier", $"批量通知注册成功：events=[{string.Join(",", RegisteredEvents)}]。");
            return;
        }

        var registered = new HashSet<byte>();
        foreach (var eventId in requestedEvents)
        {
            try
            {
                var response = await RequestAsync(
                    CommandId.RegisterNotification,
                    CommandId.RegisterNotificationResponse,
                    new byte[] { eventId },
                    cancellationToken);

                if (IsSuccess(response.Payload.Span))
                    registered.Add(eventId);
                ApplicationLog.Current?.Debug("Notifier", $"单事件通知注册：event={eventId}，success={registered.Contains(eventId)}。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        RegisteredEvents = registered;
        ApplicationLog.Current?.Info("Notifier", $"通知初始化完成：registered=[{string.Join(",", RegisteredEvents)}]。");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var subscription in _subscriptions)
            subscription.Dispose();

        _subscriptions.Clear();
    }

    // 按协议构造批量通知注册负载并检查首字节状态码。
    private async Task<bool> TryRegisterBatchAsync(byte[] eventIds, CancellationToken cancellationToken)
    {
        var payload = new byte[eventIds.Length + 1];
        payload[0] = (byte)eventIds.Length;
        eventIds.CopyTo(payload, 1);
        var response = await RequestAsync(
            CommandId.RegisterNotifications,
            CommandId.RegisterNotificationsResponse,
            payload,
            cancellationToken);

        return response.Payload.IsEmpty || IsSuccess(response.Payload.Span);
    }

    private async Task<ProtocolFrame> RequestAsync(
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        return await _channel.RequestAsync(command, responseCommand, payload, cancellationToken);
    }

    // 兼容两种通知帧格式，统一提取事件编号和事件数据。
    private void ReceiveNotification(ProtocolFrame frame)
    {
        ApplicationLog.Current?.Debug("Notifier", $"收到通知帧：command=0x{frame.Command:X4}，bytes={frame.Payload.Length}。");
        var payload = frame.Payload.Span;
        if (frame.Command == CommandId.NotificationEventResponse)
        {
            if (payload.Length < 2 || payload[0] != 0)
                return;

            Publish(payload[1], frame.Payload[2..]);
            return;
        }

        if (payload.Length > 0)
            Publish(payload[0], frame.Payload[1..]);
    }

    // 转发官方 0x0504 EQ 主动上报，不把协议帧解析职责泄漏到界面层。
    private void ReceiveEqualizerChanged(ProtocolFrame frame)
    {
        ApplicationLog.Current?.Debug(
            "Notifier",
            $"收到 EQ 变化主动上报：command=0x{frame.Command:X4}，bytes={frame.Payload.Length}。");
        var handlers = EqualizerChanged;
        if (handlers is null)
            return;

        var notification = new EqualizerChangedReceived(frame.Payload, DateTimeOffset.UtcNow);
        foreach (EventHandler<EqualizerChangedReceived> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Error("Notifier", "EQ 变化通知处理器异常。", exception);
            }
        }
    }

    // 记录事件到达时间并隔离单个订阅者的异常。
    private void Publish(byte eventId, ReadOnlyMemory<byte> data)
    {
        _lastEvents[eventId] = DateTimeOffset.UtcNow;
        ApplicationLog.Current?.Debug("Notifier", $"发布设备通知：event={eventId}，bytes={data.Length}，handlers={NotificationReceived?.GetInvocationList().Length ?? 0}。");
        var handlers = NotificationReceived;
        if (handlers is null)
            return;

        var notification = new NotificationReceived(eventId, data, _lastEvents[eventId]);
        foreach (EventHandler<NotificationReceived> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch
            {
            }
        }
    }

    private static HashSet<byte> ParseCapabilities(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0 || payload.Length < payload[1] + 2)
            return [];

        return payload.Slice(2, payload[1]).ToArray().ToHashSet();
    }

    private static bool IsSuccess(ReadOnlySpan<byte> payload) => payload.Length > 0 && payload[0] == 0;
}

public sealed record NotificationReceived(byte EventId, ReadOnlyMemory<byte> Data, DateTimeOffset ReceivedAtUtc);

// 表示设备主动上报的 EQ 状态变化帧。
public sealed record EqualizerChangedReceived(ReadOnlyMemory<byte> Payload, DateTimeOffset ReceivedAtUtc);
