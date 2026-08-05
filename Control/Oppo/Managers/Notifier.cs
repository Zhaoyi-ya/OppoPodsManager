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
    public const byte CompactnessEvent = 0x04;
    public const byte GameModeEvent = 0x05;
    public const byte MultiDeviceEvent = 0x06;
    public const byte HearingEnhancementStatusEvent = 0x08;
    public const byte EarToneEvent = 0x09;
    public const byte ZenModeEvent = 0x0A;
    public const byte PersonalizedNoiseReductionEvent = 0x0B;
    public const byte TriangleInfoChangedEvent = 0x0D;
    public const byte HearingEnhancementScanEvent = 0x0E;
    public const byte PublicEvent = 0x0F;
    public const byte OneShotEvent = 0x10;
    public const byte ToneChangeEvent = 0x11;
    public const byte ConnectDevicesEvent = 0xF2;
    public const byte DiagnosisJsonEvent = 0xF4;
    public const byte HeadMotionTypeEvent = 0xF5;
    public const byte HeadMotionTryResultEvent = 0xF6;

    // 官方 NotificationCommandManager 会注册通知能力响应中返回的全部事件，
    // 而不是只注册当前界面已经消费的几个事件。这样新固件新增事件时不会被客户端主动过滤。
    // 已知事件常量保留在这里，供功能层按需订阅；未知事件仍会通过 NotificationReceived 原样上送。

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
        // 官方会把通知能力响应中返回的完整事件集合交给注册逻辑；
        // 当前 UI 尚未消费的事件也必须注册并通过 NotificationReceived 保留，不能在这里丢弃。
        var requestedEvents = supportedEvents.OrderBy(eventId => eventId).ToArray();
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

    // 按协议构造批量通知注册负载并检查设备对 0x8205 的响应。
    // 官方批量响应通常为空 payload，因此空响应也代表注册请求已被接受；
    // 若设备实际声明支持但请求失败，则继续走官方兼容的逐事件注册路径。
    private async Task<bool> TryRegisterBatchAsync(byte[] eventIds, CancellationToken cancellationToken)
    {
        var payload = new byte[eventIds.Length + 1];
        payload[0] = (byte)eventIds.Length;
        eventIds.CopyTo(payload, 1);
        try
        {
            var response = await RequestAsync(
                CommandId.RegisterNotifications,
                CommandId.RegisterNotificationsResponse,
                payload,
                cancellationToken);

            return response.Payload.IsEmpty || IsSuccess(response.Payload.Span);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Debug("Notifier", "批量通知注册超时，降级为逐事件注册。");
            return false;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Error("Notifier", "批量通知注册失败，降级为逐事件注册。", exception);
            return false;
        }
    }

    private async Task<ProtocolFrame> RequestAsync(
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        return await _channel.RequestAsync(command, responseCommand, payload, cancellationToken);
    }

    // 兼容官方两种通知帧格式：
    // 0x0204 主动事件为 [eventId][eventData...]
    // 0x8202 注册事件响应为 [status][eventId][eventData...]
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

        // 官方 NotificationCommandManager 对 0x0204 只检查 payload 非空，
        // 不把第一个字节当作 status；否则会导致电量、佩戴和 ANC 通知全部错位。
        if (frame.Command == CommandId.NotificationEvent && payload.Length > 0)
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
