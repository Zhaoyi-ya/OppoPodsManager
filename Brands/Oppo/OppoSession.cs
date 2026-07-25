using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Core.Results;
using System.Diagnostics;
using System.Text;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Melody session after brand handshake. Owns:
/// 1) 0x0100 capability → effective features (whitelist ∩ bitmap)
/// 2) 0x0200/0x0205 notification registration
/// 3) notification freshness + explicit poll fallback plan
/// </summary>
public sealed class OppoSession : IBrandSession
{
    private const ushort QueryCapability = 0x0100;
    private const ushort CapabilityResponse = 0x8100;
    private const ushort NotifyCapabilityResponse = 0x8200;

    private readonly OppoNotificationCoordinator _notifications;
    private readonly OppoCapabilityResolver _capabilityResolver = new();
    private readonly List<byte> _rxBuffer = new();
    private readonly object _rxGate = new();
    private OppoPollPlan _pollPlan;
    private IReadOnlySet<ushort> _supportedCommands = new HashSet<ushort>();
    private bool _disposed;
    private bool _notifyQuerySent;
    private bool _initialStateQueriesSent;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private readonly Dictionary<ushort, DateTimeOffset> _lastPolls = new();

    /// <summary>Legacy static ANC bitmap fallback when whitelist AncModes miss a bit.</summary>
    private static readonly Dictionary<(byte, byte), string> AncFallbackValues = new()
    {
        [(0x01, 0)] = "Off",
        [(0x08, 0)] = "Off",
        [(0x02, 0)] = "Smart",
        [(0x80, 0)] = "Smart",
        [(0x40, 0)] = "Light",
        [(0x20, 0)] = "Medium",
        [(0x10, 0)] = "Deep",
        [(0x00, 1)] = "Transparency",
        [(0x00, 2)] = "Transparency",
        [(0x04, 0)] = "Transparency",
        [(0x00, 8)] = "Adaptive",
    };

    public OppoSession(
        IRawConnection connection,
        DeviceIdentity identity,
        DeviceCapabilities capabilities,
        OppoNotificationCoordinator? notifications = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        State = new HeadsetState { Connection = ConnectionState.Connected };
        _notifications = notifications ?? new OppoNotificationCoordinator();
        _pollPlan = OppoPollPlan.Build(capabilities.Features, new HashSet<ushort>());
        Connection.DataReceived += OnDataReceived;
        Connection.Disconnected += OnDisconnected;
    }

    public DeviceIdentity Identity { get; }
    public DeviceCapabilities Capabilities { get; private set; }
    public IRawConnection Connection { get; }
    public HeadsetState State { get; }
    public event Action? StateChanged;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifications.Reset();
        _notifyQuerySent = false;
        _initialStateQueriesSent = false;
        _lastPolls.Clear();

        // Melody order: capability first → state queries → notification registration.
        await SendCommandAsync(QueryCapability, [], cancellationToken);
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollFallbackAsync(_pollCancellation.Token);
    }

    /// <summary>
    /// Melody/legacy 0x010D payload: [count][featureId...]. Empty list is invalid.
    /// </summary>
    /// <summary>
    /// Melody PollCommandManager.k 风格：白名单声明的功能才探测。
    /// 始终探测 0x05 / 游戏 0x06+0x28，便于协议精化。
    /// </summary>
    public static byte[] BuildFeatureQueryPayload(IReadOnlySet<DeviceFeature> features)
    {
        var ids = new List<byte> { 0x05 }; // common
        if (features.Contains(DeviceFeature.DualDevice) || features.Contains(DeviceFeature.MultiDevice))
            ids.Add(0x11);
        // 始终探测新旧游戏开关，用返回结果选协议（legacy ResolveGameModeProtocol）
        ids.Add(0x06);
        ids.Add(0x28);
        if (features.Contains(DeviceFeature.SpatialAudio))
            ids.Add(0x1B);
        if (features.Contains(DeviceFeature.GameSound))
            ids.Add(0x27);
        // 仅当能力位图已裁定有专用命令时才探测，避免误显
        if (features.Contains(DeviceFeature.BassEngine))
            ids.Add(0x1D);
        if (features.Contains(DeviceFeature.VocalEnhance))
            ids.Add(0x09);
        if (features.Contains(DeviceFeature.HearingEnhance))
            ids.Add(0x0B);
        if (features.Contains(DeviceFeature.LongPowerMode))
            ids.Add(0x17);
        if (features.Contains(DeviceFeature.WearDetection))
            ids.Add(0x04);
        if (features.Contains(DeviceFeature.SpineHealth))
            ids.Add(0x22);

        var distinct = ids.Distinct().ToArray();
        return [(byte)distinct.Length, .. distinct];
    }

    public ValueTask<OperationResult> ExecuteAsync(
        DeviceCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Connection.IsConnected)
            return ValueTask.FromResult(OperationResult.Failure("原始连接未建立。"));

        if (!IsCommandAllowed(command))
            return ValueTask.FromResult(OperationResult.Failure(
                $"设备不支持命令 {command.GetType().Name}。"));

        return command switch
        {
            DeviceCommand.QueryBattery => SendOperationAsync(OppoCommandIds.QueryBattery, [], cancellationToken),
            DeviceCommand.QueryEqualizer => SendOperationAsync(OppoCommandIds.QueryEq, [], cancellationToken),
            DeviceCommand.QueryEqualizerDetails => SendOperationAsync(OppoCommandIds.QueryEqualizerDetails, [0x00], cancellationToken),
            DeviceCommand.QueryMultiDevice => SendOperationAsync(OppoCommandIds.QueryMultiDevice, [], cancellationToken),
            DeviceCommand.SetAnc anc => ApplySetAncAsync(anc.Mode, cancellationToken),
            DeviceCommand.SetSpatial spatial => ApplyFeatureSwitchAsync(
                DeviceFeature.SpatialAudio, spatial.Enabled,
                OppoCommandIds.SetFeature, EncodeFeature(0x1B, spatial.Enabled), cancellationToken),
            DeviceCommand.SetSpatialAudio spatial => ApplySetSpatialAudioAsync(spatial.Mode, cancellationToken),
            DeviceCommand.SetGameMode gaming => ApplyFeatureSwitchAsync(
                DeviceFeature.Gaming, gaming.Enabled,
                OppoCommandIds.SetFeature, EncodeFeature(GameModeFeature(), gaming.Enabled), cancellationToken),
            DeviceCommand.SetGameSound gaming => ApplyFeatureSwitchAsync(
                DeviceFeature.GameSound, gaming.Enabled,
                OppoCommandIds.SetGameSound, EncodeGameSound(gaming.Enabled), cancellationToken),
            DeviceCommand.SetFeature feature => ApplyFeatureSwitchAsync(
                feature.Feature, feature.Enabled,
                OppoCommandIds.SetFeature, EncodeFeature(FeatureId(feature.Feature), feature.Enabled), cancellationToken),
            DeviceCommand.SetEqualizer equalizer => ApplySetEqualizerAsync(equalizer.Name, cancellationToken),
            DeviceCommand.SetCustomEqualizer custom => SendOperationAsync(
                0x0418, EncodeCustomEqualizer(custom), cancellationToken),
            DeviceCommand.DeleteEqualizer deleted => SendOperationAsync(
                0x0418, EncodeDeleteEqualizer(deleted), cancellationToken),
            DeviceCommand.FindDevice find => SendOperationAsync(
                0x0400, [find.Start ? (byte)1 : (byte)0], cancellationToken),
            DeviceCommand.OperateMultiDevice multi => SendOperationAsync(
                OppoCommandIds.OperateMultiDevice, EncodeMultiDevice(multi), cancellationToken),
            _ => ValueTask.FromResult(OperationResult.Failure(
                $"Oppo 会话尚未实现命令 {command.GetType().Name}。")),
        };
    }

    /// <summary>
    /// Legacy SendAnc: 乐观更新 UI，不等设备 ACK/通知。部分型号设置成功但不回 0x8404。
    /// </summary>
    private async ValueTask<OperationResult> ApplySetAncAsync(string mode, CancellationToken cancellationToken)
    {
        var result = await SendOperationAsync(OppoCommandIds.SetAnc, EncodeAnc(mode), cancellationToken);
        if (!result.Succeeded)
            return result;

        State.Anc.Mode = mode;
        if (!string.Equals(mode, "Smart", StringComparison.Ordinal))
            State.Anc.IntelligentRealtime = null;
        StateChanged?.Invoke();

        // 后台回读校正（通知可能丢失）
        _ = SendCommandAsync(OppoCommandIds.QueryAnc, [0x01, 0x01], CancellationToken.None);
        return result;
    }

    private async ValueTask<OperationResult> ApplySetSpatialAudioAsync(string mode, CancellationToken cancellationToken)
    {
        var result = await SendOperationAsync(
            OppoCommandIds.SetSpatialAudio, EncodeSpatialMode(mode), cancellationToken);
        if (!result.Succeeded)
            return result;

        State.SpatialMode = mode;
        State.SpatialAudioEnabled = !string.Equals(mode, "Off", StringComparison.Ordinal);
        State.FeatureStates = new Dictionary<DeviceFeature, bool>(State.FeatureStates)
        {
            [DeviceFeature.SpatialAudio] = State.SpatialAudioEnabled,
        };
        StateChanged?.Invoke();
        _ = SendCommandAsync(OppoCommandIds.QuerySpatialAudio, [], CancellationToken.None);
        return result;
    }

    private async ValueTask<OperationResult> ApplySetEqualizerAsync(string name, CancellationToken cancellationToken)
    {
        var result = await SendOperationAsync(OppoCommandIds.SetEq, EncodeEqualizer(name), cancellationToken);
        if (!result.Succeeded)
            return result;

        State.Equalizer.Preset = name;
        StateChanged?.Invoke();
        _ = SendCommandAsync(OppoCommandIds.QueryEq, [], CancellationToken.None);
        return result;
    }

    private async ValueTask<OperationResult> ApplyFeatureSwitchAsync(
        DeviceFeature feature,
        bool enabled,
        ushort command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var result = await SendOperationAsync(command, payload, cancellationToken);
        if (!result.Succeeded)
            return result;

        if (feature == DeviceFeature.Gaming)
            State.GamingEnabled = enabled;
        if (feature == DeviceFeature.SpatialAudio)
            State.SpatialAudioEnabled = enabled;

        State.FeatureStates = new Dictionary<DeviceFeature, bool>(State.FeatureStates)
        {
            [feature] = enabled,
        };
        StateChanged?.Invoke();

        // 功能开关统一用 0x010D 回读校正（必须带 feature 列表）
        if (_supportedCommands.Contains(OppoCommandIds.QueryFeatureState))
            _ = SendCommandAsync(
                OppoCommandIds.QueryFeatureState,
                BuildFeatureQueryPayload(Capabilities.Features),
                CancellationToken.None);
        return result;
    }

    public void HandleFrame(ushort command, ReadOnlySpan<byte> payload)
    {
        // Always log RX so UI log pane shows real protocol traffic (not SyncConnectionStrategy spam).
        TraceRx(command, payload);

        if (command == CapabilityResponse)
        {
            HandleCapabilityResponse(payload);
            return;
        }

        if (command == NotifyCapabilityResponse)
        {
            HandleNotifyCapabilityResponse(payload);
            return;
        }

        switch (command)
        {
            case 0x8106:
                ParseBattery(payload);
                return;
            case 0x810C:
                ParseAnc(payload);
                return;
            case 0x810F:
                ParseEqualizer(payload);
                return;
            case 0x8122:
                ParseEqualizerDetails(payload);
                return;
            case 0x8105:
                ParseFirmware(payload);
                return;
            case 0x8114:
                ParseCodec(payload);
                return;
            case 0x812B:
                ParseGameSound(payload);
                return;
            case 0x812A:
                ParseSpatialAudio(payload);
                return;
            case 0x810D:
                ParseFeatureStates(payload);
                return;
            case 0x8112:
                ParseMultiDevice(payload);
                return;
            case 0x8132:
                ParseMultiPriority(payload);
                return;
        }

        // 0x8202: [status][eventId][data...]  (legacy PodManager skips status then ParseActiveReport)
        if (command == OppoNotificationCoordinator.RegisteredEvent
            && _notifications.TryParseEvent(payload, hasStatusPrefix: true, out var eventId, out var eventData))
        {
            Trace.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] notify 0x8202 event=0x{eventId:X2}({EventName(eventId)}) data={ToHex(eventData)}");
            HandleNotification(eventId, eventData);
            return;
        }

        // 0x0204: [eventId][data...]  (Melody NotificationCommandManager + legacy ParseActiveReport)
        if (command == OppoNotificationCoordinator.PushNotifyEvent
            && _notifications.TryParseEvent(payload, hasStatusPrefix: false, out var pushEventId, out var pushData))
        {
            Trace.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] notify 0x0204 event=0x{pushEventId:X2}({EventName(pushEventId)}) data={ToHex(pushData)}");
            HandleNotification(pushEventId, pushData);
            return;
        }

        if (command is OppoNotificationCoordinator.RegisterSingleResponse
            or OppoNotificationCoordinator.RegisterMultiResponse)
        {
            _notifications.HandleRegistrationResponse(command, payload);
            Trace.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] register-ack cmd=0x{command:X4} status={(payload.Length > 0 ? payload[0] : -1)}");
            return;
        }

        // Unknown frames: ignore without UI refresh.
    }

    private static void TraceRx(ushort command, ReadOnlySpan<byte> payload) =>
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] RX cmd=0x{command:X4} len={payload.Length} data={ToHex(payload)}");

    private static string ToHex(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return "(empty)";
        var take = Math.Min(data.Length, 24);
        var parts = new string[take];
        for (var i = 0; i < take; i++)
            parts[i] = data[i].ToString("X2");
        return take < data.Length
            ? string.Join("-", parts) + "…"
            : string.Join("-", parts);
    }

    private static string EventName(byte eventId) => eventId switch
    {
        OppoNotificationCoordinator.BatteryEvent => "电池",
        OppoNotificationCoordinator.WearingEvent => "佩戴",
        OppoNotificationCoordinator.NoiseModeEvent => "降噪",
        OppoNotificationCoordinator.GameModeEvent => "游戏模式",
        OppoNotificationCoordinator.MultiDeviceEvent => "多设备",
        _ => "未知",
    };

    public bool ShouldPollBattery() =>
        _notifications.ShouldPoll(OppoNotificationCoordinator.BatteryEvent, TimeSpan.FromSeconds(15));

    public bool ShouldPollAnc() =>
        _notifications.ShouldPoll(OppoNotificationCoordinator.NoiseModeEvent, TimeSpan.FromSeconds(10));

    public void ApplyProtocolCapabilities(IReadOnlySet<ushort> supportedCommands) =>
        ApplyProtocolCapabilities(OppoCapabilityBitmap.FromCommands(supportedCommands));

    private void ApplyProtocolCapabilities(OppoCapabilityBitmap bitmap)
    {
        _supportedCommands = bitmap.SupportedCommands;
        State.SupportedCommands = bitmap.SupportedCommands;
        // 白名单 = 当前 Capabilities（握手/型号识别时的静态 profile）
        var resolved = bitmap.Resolve(Capabilities);
        if (resolved.ModelName == "Unknown" && !string.IsNullOrEmpty(Identity.DisplayName))
            resolved = resolved with { ModelName = Identity.DisplayName! };
        Capabilities = resolved;
        _pollPlan = OppoPollPlan.Build(Capabilities.Features, bitmap.SupportedCommands);

        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] UI能力 features=[{string.Join(",", Capabilities.Features)}] " +
            $"spatial={(Capabilities.HasSpatialAudio ? "V2" : Capabilities.HasSpatialSound ? "V1" : "无")} " +
            $"dual={Capabilities.HasDualDevice} game={Capabilities.HasGameMode} " +
            $"gameSound={Capabilities.HasGameSound} bass={Capabilities.HasBassEngine} " +
            $"hearing={Capabilities.HasHearingEnhancement} anc={Capabilities.AncOptions.Count}opts");
        StateChanged?.Invoke();
    }

    public IReadOnlyList<OppoPollRequest> GetFallbackRequests(DateTimeOffset now)
    {
        // Caller tracks last-run timestamps; this only returns the explicit plan.
        _ = now;
        return _pollPlan.Requests
            .Where(request =>
                request.NotificationEvent is null
                || _notifications.ShouldPoll(request.NotificationEvent.Value, request.Interval))
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _pollCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (_pollTask is not null)
        {
            try
            {
                // Don't hang forever if poll is stuck in I/O.
                var finished = await Task.WhenAny(_pollTask, Task.Delay(500)).ConfigureAwait(false);
                if (finished == _pollTask)
                    await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static e =>
                e is OperationCanceledException or ObjectDisposedException))
            {
            }
        }

        try { _pollCancellation?.Dispose(); } catch { }
        _pollCancellation = null;
        _pollTask = null;

        Connection.DataReceived -= OnDataReceived;
        Connection.Disconnected -= OnDisconnected;
        State.Connection = ConnectionState.Disconnected;
        try
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        try { StateChanged?.Invoke(); }
        catch { /* UI may already be tearing down */ }
    }

    private void HandleCapabilityResponse(ReadOnlySpan<byte> payload)
    {
        // 全程由 OppoCapabilityBitmap 处理：解析 → 命令集 → 白名单求交
        var bitmap = OppoCapabilityBitmap.Parse(payload);
        bitmap.TraceLog();
        ApplyProtocolCapabilities(bitmap);

        if (!_initialStateQueriesSent)
        {
            _initialStateQueriesSent = true;
            _ = SendInitialStateQueriesAsync();
        }

        if (_notifyQuerySent)
            return;

        _ = SendCommandAsync(
            OppoNotificationCoordinator.QueryCapabilityCommand,
            [],
            CancellationToken.None);
        _notifyQuerySent = true;
    }

    /// <summary>
    /// Full initial state pull matching legacy PodManager ConnectAsync phases 2–5.
    /// Runs once after 0x0100 capability is applied.
    /// </summary>
    private async Task SendInitialStateQueriesAsync()
    {
        // Bound to session lifetime so quit/dispose does not leave orphaned work
        // throwing first-chance TaskCanceledException into the debugger.
        var ct = _pollCancellation?.Token ?? CancellationToken.None;
        try
        {
            // Small gaps avoid flooding the SPP channel (legacy used Thread.Sleep(80)).
            await Task.Delay(40, ct).ConfigureAwait(false);

            if (_supportedCommands.Contains(OppoCommandIds.QueryFeatureState))
            {
                await SendCommandAsync(
                    OppoCommandIds.QueryFeatureState,
                    BuildFeatureQueryPayload(Capabilities.Features),
                    ct).ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (Capabilities.Supports(DeviceFeature.Battery)
                && _supportedCommands.Contains(OppoCommandIds.QueryBattery))
            {
                await SendCommandAsync(OppoCommandIds.QueryBattery, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (_supportedCommands.Contains(OppoCommandIds.QueryVersion))
            {
                await SendCommandAsync(OppoCommandIds.QueryVersion, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (Capabilities.Supports(DeviceFeature.Anc)
                && _supportedCommands.Contains(OppoCommandIds.QueryAnc))
            {
                await SendCommandAsync(OppoCommandIds.QueryAnc, [0x01, 0x01], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
                // Intelligent realtime sub-level (legacy PayQueryAncIntelligent).
                if (Capabilities.AncModes?.ContainsKey("Smart") == true
                    || Capabilities.HasAdaptiveAnc)
                {
                    await SendCommandAsync(OppoCommandIds.QueryAnc, [0x04, 0x01], ct)
                        .ConfigureAwait(false);
                    await Task.Delay(40, ct).ConfigureAwait(false);
                }
            }

            if (Capabilities.Supports(DeviceFeature.Equalizer)
                && _supportedCommands.Contains(OppoCommandIds.QueryEq))
            {
                await SendCommandAsync(OppoCommandIds.QueryEq, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (Capabilities.SupportsCustomEqualizer
                && _supportedCommands.Contains(OppoCommandIds.QueryEqualizerDetails))
            {
                await SendCommandAsync(OppoCommandIds.QueryEqualizerDetails, [0x00], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (_supportedCommands.Contains(OppoCommandIds.QueryCodec))
            {
                await SendCommandAsync(OppoCommandIds.QueryCodec, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (Capabilities.Supports(DeviceFeature.GameSound)
                && _supportedCommands.Contains(OppoCommandIds.QueryGameSound))
            {
                await SendCommandAsync(OppoCommandIds.QueryGameSound, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            if (Capabilities.HasSpatialAudio
                && _supportedCommands.Contains(OppoCommandIds.QuerySpatialAudio))
            {
                await SendCommandAsync(OppoCommandIds.QuerySpatialAudio, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }

            // 双设备列表 + 优先连接策略（legacy phase 5）
            var wantMulti = Capabilities.HasDualDevice
                || Capabilities.Supports(DeviceFeature.MultiDevice)
                || Capabilities.Supports(DeviceFeature.DualDevice);
            if (wantMulti && _supportedCommands.Contains(OppoCommandIds.QueryMultiDevice))
            {
                await SendCommandAsync(OppoCommandIds.QueryMultiDevice, [], ct)
                    .ConfigureAwait(false);
                await Task.Delay(80, ct).ConfigureAwait(false);
            }

            // 0x0132 priority device — whitelist-driven, same as legacy ShouldQueryMultiPriority
            if (wantMulti || Capabilities.HasMultiConnectManage)
            {
                // 0x0132 may exist even if not listed in 0x0100 bits on some firmwares;
                // still try when dual-device capable (legacy always queries when HasDualDevice).
                await SendCommandAsync(OppoCommandIds.QueryMultiPriority, [], ct)
                    .ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested)
                Trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 初始状态查询已发送");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
        {
            // Expected on quit/dispose — do not log as failure.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 初始状态查询失败: {ex.Message}");
        }
    }

    private void HandleNotifyCapabilityResponse(ReadOnlySpan<byte> payload)
    {
        var supportsBatch = _supportedCommands.Contains(OppoNotificationCoordinator.RegisterMultiCommand);
        var registrations = _notifications.HandleCapabilityResponse(payload, supportsBatch);
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] notify-cap events=[{string.Join(",", _notifications.SupportedEvents.Select(e => $"0x{e:X2}"))}] batch={supportsBatch} register={registrations.Count}");
        foreach (var registration in registrations)
        {
            _ = SendCommandAsync(registration.Command, registration.Payload, CancellationToken.None);
        }
        // Do not StateChanged here — registration does not change headset state.
    }

    private void HandleNotification(byte eventId, ReadOnlySpan<byte> payload)
    {
        switch (eventId)
        {
            case OppoNotificationCoordinator.BatteryEvent:
                ParseBattery(payload);
                break;
            case OppoNotificationCoordinator.WearingEvent:
                ParseWearing(payload);
                break;
            case OppoNotificationCoordinator.NoiseModeEvent:
                // Notification noise payload is [kind][bitmap...], not a query response.
                ParseAncNotification(payload);
                break;
            case OppoNotificationCoordinator.GameModeEvent:
                ParseGameModeNotification(payload);
                break;
            case OppoNotificationCoordinator.MultiDeviceEvent:
                // Legacy re-queries multi-device list + priority on this event.
                _ = SendCommandAsync(OppoCommandIds.QueryMultiDevice, [], CancellationToken.None);
                _ = SendCommandAsync(OppoCommandIds.QueryMultiPriority, [], CancellationToken.None);
                break;
            default:
                // Unknown sub-type: still mark freshness but do not force UI refresh.
                return;
        }
    }

    /// <summary>
    /// 0x0204/0x8202 noise-mode event body: [kind][bitmap...].
    /// kind=1 manual, kind=4 intelligent realtime; other kinds fall back to bitmap parse.
    /// </summary>
    private void ParseAncNotification(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;

        var kind = payload[0];
        var bitmap = payload.Length > 1 ? payload[1..] : ReadOnlySpan<byte>.Empty;
        var name = ResolveNoiseBitmap(bitmap);
        if (string.IsNullOrEmpty(name) && bitmap.Length >= 2)
            name = ResolveNoiseBitmap(payload); // some firmwares omit kind or embed bitmap from offset 0

        if (string.IsNullOrEmpty(name))
        {
            // Still unreadable → query current mode (legacy behavior).
            _ = SendCommandAsync(OppoCommandIds.QueryAnc, [0x01, 0x01], CancellationToken.None);
            return;
        }

        if (kind == 4)
        {
            State.Anc.Mode = "Smart";
            State.Anc.IntelligentRealtime = name;
        }
        else
        {
            State.Anc.Mode = name;
            if (!string.Equals(name, "Smart", StringComparison.Ordinal))
                State.Anc.IntelligentRealtime = null;
        }

        StateChanged?.Invoke();
    }

    private void ParseGameModeNotification(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;

        var enabled = payload[0] != 0;
        State.GamingEnabled = enabled;
        State.FeatureStates = new Dictionary<DeviceFeature, bool>(State.FeatureStates)
        {
            [DeviceFeature.Gaming] = enabled,
        };
        StateChanged?.Invoke();
    }

    private void ParseBattery(ReadOnlySpan<byte> payload)
    {
        // Strip optional status=0 prefix used by 0x8106 query responses.
        // Real Air5 Pro log: 00-02-01-5A-02-5A  → status + [count][idx][raw]...
        var data = payload;
        if (data.Length >= 2 && data[0] == 0)
            data = data[1..];

        // Fixed SPP layout: [count=4][L_lev][L_chg][R_lev][R_chg][C_lev][C_chg]
        if (data.Length >= 7 && data[0] == 4)
        {
            State.Battery.Left = data[1];
            State.Battery.LeftCharging = data[2] != 0;
            State.Battery.Right = data[3];
            State.Battery.RightCharging = data[4] != 0;
            State.Battery.Case = data[5];
            State.Battery.CaseCharging = data[6] != 0;
            StateChanged?.Invoke();
            return;
        }

        // List form: [count][idx][raw][idx][raw]...
        if (data.Length >= 2)
        {
            var count = data[0];
            if (count > 0 && count <= 8 && data.Length >= 1 + count * 2)
            {
                for (var j = 0; j < count; j++)
                    ApplyBatteryEntry(data[1 + j * 2], data[2 + j * 2]);
                StateChanged?.Invoke();
                return;
            }
        }

        for (var i = 0; i + 1 < data.Length; i += 2)
            ApplyBatteryEntry(data[i], data[i + 1]);

        StateChanged?.Invoke();
    }

    private void ApplyBatteryEntry(byte index, byte raw)
    {
        var level = raw & 0x7F;
        var charging = (raw & 0x80) != 0;
        switch (index)
        {
            case 1: State.Battery.Left = level; State.Battery.LeftCharging = charging; break;
            case 2: State.Battery.Right = level; State.Battery.RightCharging = charging; break;
            case 3: State.Battery.Case = level; State.Battery.CaseCharging = charging; break;
        }
    }

    private void ParseAnc(ReadOnlySpan<byte> payload)
    {
        // 0x810C query responses are often status-prefixed: 00-01-01-10-00 / 00-04-01-40-00.
        // Notifications (0x0204 event 0x03) are bare: 01-01-10-00.
        var data = payload;
        if (data.Length >= 2 && data[0] == 0)
            data = data[1..];

        // Intelligent realtime sub-level: 0x04 0x01 + noise bitmap.
        if (data.Length >= 4 && data[0] == 0x04 && data[1] == 0x01)
        {
            var realtime = ResolveNoiseBitmap(data[2..]);
            State.Anc.IntelligentRealtime =
                !string.IsNullOrEmpty(realtime) && !string.Equals(realtime, "Smart", StringComparison.Ordinal)
                    ? realtime
                    : null;
            // Keep Mode as Smart when device reports intelligent sub-level.
            if (!string.IsNullOrEmpty(realtime))
                State.Anc.Mode = "Smart";
            StateChanged?.Invoke();
            return;
        }

        // Standard mode query: 0x01 0x01 + little-endian mode bitmap.
        if (data.Length >= 4 && data[0] == 0x01 && data[1] == 0x01)
        {
            var mode = ResolveNoiseBitmap(data[2..]);
            if (!string.IsNullOrEmpty(mode))
            {
                State.Anc.Mode = mode;
                if (!string.Equals(mode, "Smart", StringComparison.Ordinal))
                    State.Anc.IntelligentRealtime = null;
            }

            StateChanged?.Invoke();
            return;
        }

        // Notification noise-change / bare body:
        // [kind][info...] kind=1 manual, kind=4 intelligent realtime.
        if (data.Length >= 3)
        {
            var kind = data[0];
            var name = ResolveNoiseBitmap(data[1..]);
            if (string.IsNullOrEmpty(name) && data.Length >= 2)
                name = ResolveNoiseBitmap(data); // some firmwares omit kind

            if (!string.IsNullOrEmpty(name))
            {
                if (kind == 4)
                {
                    State.Anc.Mode = "Smart";
                    State.Anc.IntelligentRealtime = name;
                }
                else
                {
                    State.Anc.Mode = name;
                    if (!string.Equals(name, "Smart", StringComparison.Ordinal))
                        State.Anc.IntelligentRealtime = null;
                }

                StateChanged?.Invoke();
            }
        }
    }

    private string? ResolveNoiseBitmap(ReadOnlySpan<byte> bitmap)
    {
        if (bitmap.Length < 2)
            return null;

        // Official layout: type(1)=1 + little-endian value bytes.
        // Some firmwares omit type and send raw 2-byte LE value.
        var offset = 0;
        if (bitmap[0] == 1 && bitmap.Length >= 3)
            offset = 1;
        if (offset >= bitmap.Length)
            return null;

        var value = 0;
        for (var index = 0; offset + index < bitmap.Length && index < 4; index++)
            value |= bitmap[offset + index] << (index * 8);

        if (Capabilities.AncModes is { Count: > 0 })
        {
            for (var bit = 0; bit < 32; bit++)
            {
                if ((value & (1 << bit)) == 0)
                    continue;

                var mode = Capabilities.AncModes.FirstOrDefault(pair => pair.Value == bit).Key;
                if (!string.IsNullOrEmpty(mode))
                    return mode;
            }
        }

        // Static fallback (legacy OppoProtocol.AncValues) — needed when whitelist
        // AncModes is empty/incomplete so Transparency is not misread as Off.
        var v1 = bitmap[offset];
        var v2 = offset + 1 < bitmap.Length ? bitmap[offset + 1] : (byte)0;
        if (AncFallbackValues.TryGetValue((v1, v2), out var fallback))
            return fallback;

        // Bit-scan fallback for single-bit maps without whitelist.
        for (var bit = 0; bit < 16; bit++)
        {
            if ((value & (1 << bit)) == 0)
                continue;
            return bit switch
            {
                0 => "Off",
                1 => "Smart",
                2 => "Transparency",
                3 => "NC",
                4 => "Deep",
                5 => "Medium",
                6 => "Light",
                7 => "Smart",
                11 => "Adaptive",
                _ => null,
            };
        }

        return null;
    }

    private void ParseEqualizer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return;

        var id = payload[1];
        var name = Capabilities.EqualizerPresets?.FirstOrDefault(pair => pair.Value == id).Key
                   ?? State.DeviceEqualizers.FirstOrDefault(entry => entry.Id == id)?.Name;
        State.Equalizer.Preset = name;
        StateChanged?.Invoke();
    }

    private void ParseEqualizerDetails(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return;

        var count = payload[1];
        var offset = 2;
        var entries = new List<EqualizerEntry>();
        for (var index = 0; index < count && offset + 5 <= payload.Length; index++)
        {
            var selected = payload[offset] != 0;
            var minimum = unchecked((sbyte)payload[offset + 1]);
            var maximum = unchecked((sbyte)payload[offset + 2]);
            var id = payload[offset + 3];
            var nameLength = payload[offset + 4];
            offset += 5;
            if (offset + nameLength > payload.Length)
                break;

            var name = Encoding.UTF8.GetString(payload.Slice(offset, nameLength)).Trim();
            offset += nameLength;
            if (offset >= payload.Length)
                break;

            var bandCount = payload[offset++];
            var frequencies = new int[bandCount];
            var gains = new int[bandCount];
            for (var band = 0; band < bandCount; band++)
            {
                if (offset + 3 > payload.Length)
                    return;
                frequencies[band] = payload[offset] | payload[offset + 1] << 8;
                gains[band] = unchecked((sbyte)payload[offset + 2]);
                offset += 3;
            }

            entries.Add(new EqualizerEntry(id, name, frequencies, gains, minimum, maximum, selected));
            if (selected)
                State.Equalizer.Preset = name;
        }

        State.DeviceEqualizers = entries;
        StateChanged?.Invoke();
    }

    private void ParseFirmware(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 3 && payload[0] == 0)
            State.FirmwareVersion = Encoding.UTF8.GetString(payload[2..]).TrimEnd('\0').Trim();
        StateChanged?.Invoke();
    }

    private void ParseCodec(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 2 && payload[0] == 0)
            State.CodecType = payload[1];
        else if (payload.Length >= 4 && payload[0] == 0)
        {
            var count = payload[1];
            for (var index = 0; index < count && 3 + index * 2 < payload.Length; index++)
            {
                if (payload[3 + index * 2] != 0)
                {
                    State.CodecType = payload[2 + index * 2];
                    break;
                }
            }
        }
        StateChanged?.Invoke();
    }

    private void ParseGameSound(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return;

        // 0x812B is game *sound* selectType, not game *mode* (latency).
        var selectedType = payload[1];
        var enabled = selectedType != 0;
        State.FeatureStates = new Dictionary<DeviceFeature, bool>(State.FeatureStates)
        {
            [DeviceFeature.GameSound] = enabled,
        };
        StateChanged?.Invoke();
    }

    private void ParseWearing(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            return;

        var count = payload[1];
        for (var index = 0; index < count && 3 + index * 2 < payload.Length; index++)
        {
            var component = payload[2 + index * 2];
            var status = payload[3 + index * 2];
            var text = status switch
            {
                0 => "已断连",
                1 or 5 => "摘下",
                3 or 7 => "佩戴",
                4 => "入盒",
                _ => $"未知({status})",
            };
            if (component == 1)
                State.LeftWearing = text;
            else if (component == 2)
                State.RightWearing = text;
        }
        StateChanged?.Invoke();
    }

    private void ParseSpatialAudio(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2)
        {
            var type = payload[1];
            State.SpatialAudioEnabled = type != 0;
            State.SpatialMode = type switch
            {
                1 => "Fixed",
                2 => "Track",
                _ => "Off",
            };
        }

        StateChanged?.Invoke();
    }

    private void ParseFeatureStates(ReadOnlySpan<byte> payload)
    {
        // Real Air5 Pro: 00-09-05-01-11-01-06-00-28-00-1B-00-27-00-1D-00-0B-00-04-00
        // = [status=0][count=9][feature,value]×9
        var data = payload;
        if (data.Length >= 2 && data[0] == 0)
        {
            var rest = data[1..];
            if (rest.Length >= 1)
            {
                var maybeCount = rest[0];
                if (maybeCount > 0 && maybeCount <= 32 && rest.Length == 1 + maybeCount * 2)
                    data = rest[1..];
                else
                    data = rest;
            }
        }

        var states = new Dictionary<DeviceFeature, bool>(State.FeatureStates);
        var returned = new HashSet<byte>();
        byte? gameMain = null;
        byte? gameLl = null;
        var changed = false;

        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var feature = data[i];
            var enabled = data[i + 1] != 0;
            returned.Add(feature);

            switch (feature)
            {
                case 0x28: // FeatureGameMain
                    gameMain = data[i + 1];
                    break;
                case 0x06: // FeatureGameLL
                    gameLl = data[i + 1];
                    break;
                case 0x27: // FeatureGameSound
                    states[DeviceFeature.GameSound] = enabled;
                    changed = true;
                    break;
                case 0x1B: // FeatureSpatial (V1 switch)
                    State.SpatialAudioEnabled = enabled;
                    states[DeviceFeature.SpatialAudio] = enabled;
                    changed = true;
                    break;
                case 0x11: // FeatureDualDevice
                    states[DeviceFeature.MultiDevice] = enabled;
                    states[DeviceFeature.DualDevice] = enabled;
                    changed = true;
                    break;
                case 0x1D: // FeatureBassEngine — only if capability already allows
                    if (Capabilities.Supports(DeviceFeature.BassEngine))
                    {
                        states[DeviceFeature.BassEngine] = enabled;
                        changed = true;
                    }
                    break;
                case 0x09: // FeatureVocalEnhance
                    if (Capabilities.Supports(DeviceFeature.VocalEnhance))
                    {
                        states[DeviceFeature.VocalEnhance] = enabled;
                        changed = true;
                    }
                    break;
                case 0x0B: // FeatureHearingEnhance
                    if (Capabilities.Supports(DeviceFeature.HearingEnhance))
                    {
                        states[DeviceFeature.HearingEnhance] = enabled;
                        changed = true;
                    }
                    break;
                case 0x17: // FeatureLongPowerMode
                    if (Capabilities.Supports(DeviceFeature.LongPowerMode))
                    {
                        states[DeviceFeature.LongPowerMode] = enabled;
                        changed = true;
                    }
                    break;
                case 0x04: // FeatureWearDetection
                    if (Capabilities.Supports(DeviceFeature.WearDetection))
                    {
                        states[DeviceFeature.WearDetection] = enabled;
                        changed = true;
                    }
                    break;
                case 0x22: // FeatureSpineLiveMonitor
                    if (Capabilities.Supports(DeviceFeature.SpineHealth))
                    {
                        states[DeviceFeature.SpineHealth] = enabled;
                        changed = true;
                    }
                    break;
            }
        }

        // Melody ResolveGameModeProtocol: prefer 0x28 when game-sound device, else 0x06.
        if (gameMain is not null || gameLl is not null)
        {
            byte featureId;
            bool enabled;
            if (Capabilities.HasGameSound && gameMain is not null)
            {
                featureId = 0x28;
                enabled = gameMain.Value != 0;
            }
            else if (gameLl is not null)
            {
                featureId = 0x06;
                enabled = gameLl.Value != 0;
            }
            else
            {
                featureId = 0x28;
                enabled = gameMain is > 0;
            }

            State.GamingEnabled = enabled;
            states[DeviceFeature.Gaming] = enabled;
            if (Capabilities.GameModeFeature != featureId)
                Capabilities = Capabilities.WithGameModeFeature(featureId);
            changed = true;
        }

        // 0x810D 未返回的专用开关：从 FeatureStates 清除，避免 UI 残留。
        // 注意：仅清理“专用 feature id”，不因未查询而删 Dual/Game 等核心项。
        foreach (var orphan in new[]
                 {
                     (DeviceFeature.BassEngine, (byte)0x1D),
                     (DeviceFeature.HearingEnhance, (byte)0x0B),
                     (DeviceFeature.VocalEnhance, (byte)0x09),
                     (DeviceFeature.LongPowerMode, (byte)0x17),
                     (DeviceFeature.SpineHealth, (byte)0x22),
                 })
        {
            if (!returned.Contains(orphan.Item2) && states.Remove(orphan.Item1))
                changed = true;
        }

        if (!changed)
            return;

        State.FeatureStates = states;
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 0x810D features=[{string.Join(",", returned.OrderBy(x => x).Select(x => $"0x{x:X2}"))}] " +
            $"states=[{string.Join(",", states.Select(kv => $"{kv.Key}={(kv.Value ? 1 : 0)}"))}]");
        StateChanged?.Invoke();
    }

    private void ParseMultiDevice(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return;

        var count = payload[1];
        if (count <= 0 || count > 8)
            return;

        var devices = new List<MultiDeviceEntry>();
        var addresses = new List<string>();
        var offset = 2;
        for (var index = 0; index < count && offset + 9 <= payload.Length; index++)
        {
            // MAC is little-endian on the wire (same as legacy ParseMultiConnect).
            var addressParts = new string[6];
            for (var part = 0; part < addressParts.Length; part++)
                addressParts[part] = payload[offset + 5 - part].ToString("X2");
            var address = string.Join(":", addressParts);
            offset += 6;

            if (offset + 3 > payload.Length)
                break;

            var deviceType = payload[offset++];
            var connectionState = payload[offset++];
            var flag = payload[offset++];
            if (offset >= payload.Length)
                break;

            var nameLength = payload[offset++];
            if (nameLength < 0 || offset + nameLength > payload.Length)
                break;

            string name;
            if (nameLength > 0)
            {
                name = Encoding.UTF8.GetString(payload.Slice(offset, nameLength)).TrimEnd('\0').Trim();
            }
            else
            {
                var suffix = address.Length <= 5 ? address : address.Substring(address.Length - 5);
                name = "Device " + suffix;
            }
            offset += nameLength;

            var isCurrent = (flag & 0x01) != 0;
            var isMainAudio = (flag & 0x02) != 0;
            var isAudioActive = (flag & 0x04) != 0;

            addresses.Add(address);
            devices.Add(new MultiDeviceEntry(
                address,
                name,
                connectionState,
                deviceType,
                isCurrent,
                isAudioActive,
                isMainAudio));
        }

        if (devices.Count == 0)
            return;

        devices = devices
            .OrderByDescending(entry => entry.IsCurrentDevice)
            .ThenBy(entry => entry.Name ?? entry.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();

        State.MultiDevice.Devices = devices;
        State.MultiDevice.ConnectedAddresses = addresses;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Parse 0x8132 getMultiConnectPriorityDevice (legacy ParseMultiPriority).
    /// Layout: optional status=0, then [level][mode][mac? LE].
    /// level 1=low / 2=high; mode==0 → automatic priority.
    /// </summary>
    private void ParseMultiPriority(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return;

        var offset = 0;
        // Optional status prefix when next byte is a valid level (1/2).
        if (payload.Length >= 3 && payload[0] == 0x00
            && (payload[1] == 1 || payload[1] == 2))
        {
            offset = 1;
        }
        else if (payload[0] != 1 && payload[0] != 2)
        {
            Trace.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] ParseMultiPriority: 无法识别布局 data={ToHex(payload)}");
            return;
        }

        var remain = payload.Length - offset;
        if (remain < 2)
            return;

        var modeByte = payload[offset + 1];
        var autoMode = modeByte == 0;
        string? priorityAddr = null;

        if (!autoMode && remain >= 8)
        {
            // MAC little-endian → display order (same as ParseMultiDevice)
            var parts = new string[6];
            for (var j = 0; j < 6; j++)
                parts[j] = payload[offset + 2 + 5 - j].ToString("X2");
            priorityAddr = string.Join(":", parts);
        }

        // mode!=0 but no MAC → fall back to auto (Melody APK behavior)
        if (!autoMode && string.IsNullOrEmpty(priorityAddr))
        {
            autoMode = true;
            priorityAddr = null;
        }

        State.MultiDevice.AutomaticPriority = autoMode;
        State.MultiDevice.PriorityAddress = priorityAddr;
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] multi-priority auto={autoMode} addr={priorityAddr ?? "(none)"}");
        StateChanged?.Invoke();
    }

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        lock (_rxGate)
        {
            _rxBuffer.AddRange(data.ToArray());
            while (TryReadFrame(_rxBuffer, out var command, out var payload))
                HandleFrame(command, payload);
        }
    }

    private void OnDisconnected()
    {
        State.Connection = ConnectionState.Disconnected;
        StateChanged?.Invoke();
    }

    private async ValueTask SendCommandAsync(
        ushort command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (_disposed || !Connection.IsConnected)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await Connection.SendAsync(OppoFrameCodec.Encode(command, payload), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Session/connection already torn down.
        }
    }

    private async Task PollFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return;

                foreach (var request in GetFallbackRequests(DateTimeOffset.UtcNow))
                {
                    if (_lastPolls.TryGetValue(request.Command, out var last)
                        && DateTimeOffset.UtcNow - last < request.Interval)
                        continue;

                    try
                    {
                        await SendCommandAsync(request.Command, request.Payload, cancellationToken)
                            .ConfigureAwait(false);
                        _lastPolls[request.Command] = DateTimeOffset.UtcNow;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        // The raw connection owns disconnect/error notification. A single
                        // failed fallback query must not terminate the session loop.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // WaitForNextTickAsync throws on cancel — normal shutdown path.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async ValueTask<OperationResult> SendOperationAsync(
        ushort command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendCommandAsync(command, payload, cancellationToken);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }
    }

    private static byte[] EncodeSpatialMode(string mode) =>
        [mode switch
        {
            "Fixed" => (byte)1,
            "Track" => (byte)2,
            _ => (byte)0,
        }];

    private static byte[] EncodeFeature(byte feature, bool enabled) =>
        [feature, enabled ? (byte)1 : (byte)0];

    private byte[] EncodeGameSound(bool enabled) =>
        [enabled ? (Capabilities.GameSoundType == 0 ? (byte)1 : Capabilities.GameSoundType) : (byte)0, 1];

    private byte GameModeFeature() =>
        Capabilities.GameModeFeature != 0
            ? Capabilities.GameModeFeature
            : Capabilities.Features.Contains(DeviceFeature.Gaming) ? (byte)0x28 : (byte)0x06;

    private byte[] EncodeAnc(string mode)
    {
        if (Capabilities.AncModes is not null
            && Capabilities.AncModes.TryGetValue(mode, out var protocolIndex))
        {
            var byteCount = protocolIndex / 8 + 1;
            var payload = new byte[2 + byteCount];
            payload[0] = 0x01;
            payload[1] = 0x01;
            payload[2 + protocolIndex / 8] = (byte)(1 << (protocolIndex % 8));
            return payload;
        }

        return mode switch
        {
            "Smart" => [0x01, 0x01, 0x80],
            "Light" => [0x01, 0x01, 0x40],
            "Medium" => [0x01, 0x01, 0x20],
            "Deep" => [0x01, 0x01, 0x10],
            "Adaptive" => [0x01, 0x01, 0x00, 0x08],
            "Transparency" => [0x01, 0x01, 0x04],
            _ => [0x01, 0x01, 0x01],
        };
    }

    private byte[] EncodeEqualizer(string name)
    {
        if (Capabilities.EqualizerPresets is not null
            && Capabilities.EqualizerPresets.TryGetValue(name, out var preset))
            return [preset];

        var entry = State.DeviceEqualizers.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        return entry is null ? [] : [entry.Id];
    }

    private static byte FeatureId(DeviceFeature feature) => feature switch
    {
        DeviceFeature.Gaming => 0x28,
        DeviceFeature.GameSound => 0x27,
        DeviceFeature.MultiDevice => 0x11,
        DeviceFeature.DualDevice => 0x11,
        DeviceFeature.TouchControls => 0x04,
        DeviceFeature.WearDetection => 0x04,
        DeviceFeature.FindDevice => 0x00,
        DeviceFeature.SpatialAudio => 0x1B,
        DeviceFeature.BassEngine => 0x1D,
        DeviceFeature.VocalEnhance => 0x09,
        DeviceFeature.HearingEnhance => 0x0B,
        DeviceFeature.LongPowerMode => 0x17,
        DeviceFeature.SpineHealth => 0x22,
        _ => 0x00,
    };

    private bool IsCommandAllowed(DeviceCommand command) => command switch
    {
        DeviceCommand.QueryBattery => Capabilities.Supports(DeviceFeature.Battery),
        DeviceCommand.SetAnc => Capabilities.Supports(DeviceFeature.Anc),
        DeviceCommand.SetSpatialAudio => Capabilities.Supports(DeviceFeature.SpatialAudio),
        DeviceCommand.SetSpatial => Capabilities.Supports(DeviceFeature.SpatialAudio),
        DeviceCommand.SetGameMode => Capabilities.Supports(DeviceFeature.Gaming),
        DeviceCommand.SetGameSound => Capabilities.Supports(DeviceFeature.Gaming),
        DeviceCommand.SetEqualizer => Capabilities.Supports(DeviceFeature.Equalizer),
        DeviceCommand.SetCustomEqualizer => Capabilities.SupportsCustomEqualizer,
        DeviceCommand.DeleteEqualizer => Capabilities.SupportsCustomEqualizer,
        DeviceCommand.QueryEqualizer => Capabilities.Supports(DeviceFeature.Equalizer),
        DeviceCommand.QueryMultiDevice => Capabilities.Supports(DeviceFeature.MultiDevice),
        DeviceCommand.OperateMultiDevice => Capabilities.SupportsMultiDevice,
        DeviceCommand.FindDevice => Capabilities.Supports(DeviceFeature.FindDevice),
        DeviceCommand.SetFeature feature => Capabilities.Supports(feature.Feature),
        _ => false,
    };

    private static byte[] EncodeCustomEqualizer(DeviceCommand.SetCustomEqualizer command)
    {
        var frequencies = command.Frequencies ??
            Enumerable.Range(0, command.Gains.Count).Select(index => index switch
            {
                0 => 62,
                1 => 250,
                2 => 1000,
                3 => 4000,
                4 => 8000,
                _ => 16000,
            }).ToArray();
        var nameBytes = Encoding.UTF8.GetBytes(command.Name);
        var count = Math.Min(command.Gains.Count, frequencies.Count);
        var payload = new List<byte>(8 + count * 3 + nameBytes.Length)
        {
            command.Id is null ? (byte)1 : (byte)2,
            (byte)command.Minimum,
            (byte)command.Maximum,
            command.Id ?? 0,
            (byte)nameBytes.Length,
        };
        payload.AddRange(nameBytes);
        payload.Add((byte)count);
        for (var index = 0; index < count; index++)
        {
            var frequency = Math.Clamp(frequencies[index], short.MinValue, ushort.MaxValue);
            payload.Add((byte)frequency);
            payload.Add((byte)(frequency >> 8));
            payload.Add(unchecked((byte)Math.Clamp(command.Gains[index], sbyte.MinValue, sbyte.MaxValue)));
        }

        return payload.ToArray();
    }

    private static byte[] EncodeDeleteEqualizer(DeviceCommand.DeleteEqualizer command)
    {
        if (command.Entry is null)
            return [3, command.Id];

        var update = new DeviceCommand.SetCustomEqualizer(
            command.Entry.Gains,
            command.Entry.Name,
            command.Entry.Id,
            command.Entry.Frequencies,
            command.Entry.Minimum,
            command.Entry.Maximum);
        var payload = EncodeCustomEqualizer(update);
        payload[0] = 3;
        return payload;
    }

    private static byte[] EncodeMultiDevice(DeviceCommand.OperateMultiDevice command)
    {
        var address = command.Address.Replace(":", "", StringComparison.Ordinal);
        var mac = new byte[6];
        for (var i = 0; i < 6 && i * 2 + 1 < address.Length; i++)
            mac[i] = Convert.ToByte(address.Substring(i * 2, 2), 16);

        var operation = command.Operation switch
        {
            MultiDeviceOperation.Connect => (byte)1,
            MultiDeviceOperation.Disconnect => (byte)2,
            MultiDeviceOperation.Unpair => (byte)3,
            _ => (byte)4,
        };

        if (operation == 4)
            return command.Operation == MultiDeviceOperation.AutoSwitch
                ? [4, 0]
                : [4, 1, .. mac];

        return [operation, .. mac];
    }

    private static bool TryReadFrame(List<byte> buffer, out ushort command, out byte[] payload)
    {
        command = 0;
        payload = [];
        var start = buffer.IndexOf(0xAA);
        if (start < 0)
        {
            buffer.Clear();
            return false;
        }

        if (start > 0)
            buffer.RemoveRange(0, start);
        if (buffer.Count < 2)
            return false;

        var frameLength = buffer[1] + 2;
        if (frameLength < 9 || frameLength > 4096)
        {
            buffer.RemoveAt(0);
            return false;
        }

        if (buffer.Count < frameLength)
            return false;

        command = (ushort)(buffer[4] | (buffer[5] << 8));
        var payloadLength = buffer[7] | (buffer[8] << 8);
        if (payloadLength < 0 || payloadLength > frameLength - 9)
        {
            buffer.RemoveRange(0, frameLength);
            return false;
        }

        payload = buffer.GetRange(9, payloadLength).ToArray();
        buffer.RemoveRange(0, frameLength);
        return true;
    }
}
