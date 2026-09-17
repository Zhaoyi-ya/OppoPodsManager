using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// MVP：验证「发现 → RFCOMM 连接 → 认证 → 电量读取」通路。
// 能力集全部置空（DeviceCapability.Unknown），UI 据此不显示任何高级功能控件（降噪/EQ/手势等）。
// 降噪、EQ、手势等协议将在能力表（XiaomiConstants 的 DeviceConfigId + Get/SetDeviceConfig）打通后补全。
//
// 2026-09-14 修正：
//   · 帧结构已按官方包证据改正（帧头 3 字节、帧尾单字节 0xEF、无 CRC16），见 XiaomiFrameCodec 注释。
//   · 认证已接入：XiaomiAuth 的算法与 libxm_bluetooth.so 逐字节一致，报文负载布局由官方字节码还原，
//     命令字为 RCSP 的 CmdAuthCheck(80) / CmdAuthSendCalcResult(81)。
public sealed class XiaomiManager : BrandManagerBase, IBrandManager
{
    // 认证探测窗口。官方 RequestAsync 的超时固定 4 秒，对「不要求认证」的机型代价太大，
    // 故此处单独限一个短窗口；超时即视为不需要认证，不影响后续电量读取。
    private static readonly TimeSpan AuthProbeTimeout = TimeSpan.FromMilliseconds(1200);

    private readonly XiaomiConfigChannel _config = new();
    private DeviceCapability _capability = DeviceCapability.Unknown;
    private ConnectionLink? _link;

    // 控制项状态：Presentation.ControlStates 的数据源，UI 复选框据此勾选。
    // 键名须与 UI 侧的判断一致（HomeView.axaml.cs 用的是 visibleControls.Contains("game-mode")）。
    private readonly Dictionary<string, bool> _controlStates = new(StringComparer.Ordinal)
    {
        ["game-mode"] = false
    };

    public XiaomiManager()
    {
        State.Changed += PublishState;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;
    public BusinessSnapshot Snapshot => State.Snapshot();
    public DeviceCapability Capability { get => _capability; private set => _capability = value; }
    public bool CanManageMultiDevice => false;
    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public IEqualizerProfile EqualizerProfile => NullEqualizerProfile.Instance;

    // 控制项可见性：当前只开放「游戏模式（低延迟）」——它是唯一具备**完整外部证据**的功能
    // （配置 ID 47 + 值 01/00，见 XiaomiConfigChannel 的注释与依据）。
    //
    // 降噪 / EQ / 多点连接 / 空间音频的配置 ID 已在 XiaomiConstants 就位，但**取值编码尚未确认**，
    // 因此在真机验证前不写入 VisibleControls —— 避免 UI 放出"点了没反应"的控件。
    // 待配置通道验证通过后，按 ID 表逐个补齐即可（通道已通，剩下的是填表工作）。
    private static readonly IReadOnlySet<string> VisibleControls =
        new HashSet<string>(StringComparer.Ordinal) { "game-mode" };

    public BrandPresentation Presentation
    {
        get
        {
            var states = new Dictionary<string, bool>(_controlStates, StringComparer.Ordinal);
            return new BrandPresentation(
                Capability.ModelName,
                Capability.IsKnownModel,
                false,                          // SupportsSpatialAudio
                false,                          // SupportsCustomEqualizer
                false,                          // SupportsNoiseCancellation
                false,                          // CanManageMultiDevice
                Array.Empty<ushort>(),
                CustomEqualizerMinimumGain,
                CustomEqualizerMaximumGain,
                Array.Empty<string>(),
                VisibleControls,
                states,
                states,                         // ControlEnabledStates：暂不置灰任何项
                Array.Empty<NoiseOptionModel>(),
                string.Empty);
        }
    }

    public void SetInteractivePolling(bool enabled) => InteractivePolling = enabled;

    public void SetManualModel(string? modelName)
    {
        // MVP：无型号库识别，忽略手动型号覆盖。
    }

    // ---- 会话：连接 → 认证探测 → 电量读取 ----
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _link = link;
        link.Disconnected += OnLinkDisconnected;
        State.SetConnected(deviceName);

        // 认证先行：要求在认证后才应答其它命令的机型，先认证可让后续读取一次成功。
        // 该步骤失败不影响会话（详见 TryAuthenticateAsync）。
        var authenticated = await TryAuthenticateAsync(link, deviceName, cancellationToken);

        // 认证结果上报（opCode 0x51）。依据外部实现 MiBudsClient：它在连接建立后固定发送
        // `FE DC BA 04 51 0003 00 03 01 EF`。本项目已单独完成挑战/应答，这一步是否必需尚未确认，
        // 故只在认证成功时补发，失败静默忽略、不阻断会话。
        if (authenticated)
            await _config.TrySendAuthResultAsync(link, cancellationToken);

        ApplicationLog.Current?.Info("Xiaomi", $"已建立 RFCOMM 会话：{deviceName}，开始尝试读取电量。");
        try
        {
            var response = await link.RequestAsync(
                XiaomiConstants.BatteryQueryCommand,
                XiaomiConstants.BatteryResponseCommand,
                XiaomiConstants.BatteryQueryPayload,
                cancellationToken);
            // 单电量设备（头戴式）只回整机电量，负载比真无线更短，故下限放宽到 5 字节；
            // 超出实际负载长度的槽位留空，电量卡据此自动渲染为单栏或两栏。
            if (response is not null && response.Payload.Length >= 5)
            {
                var p = response.Payload.Span;
                // 电量应答负载 = 02 02 04 07 L R C（对齐 MiBudsClient 的 BATTERY_PATTERN=02020407，
                // L/R/C 在模式字节之后）。
                var right = p.Length > 5 ? Clamp(p[5]) : (byte?)null;
                var caseLevel = p.Length > 6 ? Clamp(p[6]) : (byte?)null;
                State.SetBattery(
                    new BatteryLevel(Clamp(p[4]), false),
                    right.HasValue ? new BatteryLevel(right.Value, false) : null,
                    caseLevel.HasValue ? new BatteryLevel(caseLevel.Value, false) : null);
                ApplicationLog.Current?.Info("Xiaomi",
                    $"电量读取成功：L={p[4]} R={right?.ToString() ?? "-"} C={caseLevel?.ToString() ?? "-"}。");
            }
            else
            {
                ApplicationLog.Current?.Info("Xiaomi",
                    "电量请求未收到有效响应（命令通道可能无需认证，或帧/响应格式待真机校准）。");
            }
        }
        catch (Exception ex)
        {
            ApplicationLog.Current?.Error("Xiaomi", $"电量读取失败：{ex.Message}", ex);
        }

        // 会话握手验证：电量查询没有收到过“匹配命令字的应答” = 死通道（其它品牌设备的裸通道会发
        // 非协议噪声字节，仅凭 LastReceiveTicks==0 拦不住），改用 LastResponseTicks==0 判死通道，
        // 抛 ChannelUnusableException 让 Discovery 切换下一品牌，而不是建一个空会话假在线。
        // 注：认证探测走的是 RawDataReceived，不计入 LastResponseTicks；只有 RequestAsync 收到的
        // 匹配应答才计。因此认证成功但设备始终不应答电量时，仍会如实判为死通道。
        if (link.LastResponseTicks == 0)
            throw new ChannelUnusableException(
                $"小米协议握手未收到任何应答（电量查询无应答），疑似落到非小米通道：{deviceName}。");
    }

    // 认证挑战/应答。
    //
    // 依据（均为官方包内证据，非推测）：
    //   · 算法：XiaomiAuth.GetEncryptedAuthCheckData，与 libxm_bluetooth.so（arm64/v7a 双 ABI）
    //     的 SBOX/T1/T2 三张 256 字节表及默认 linkKey 逐字节一致。
    //   · 请求负载：[version=0x01] || randomFactor(16) = 17 字节，取自 AuthCheckParam.getParamData()。
    //   · 应答负载：[versionResponse(1)] || result(16) = 17 字节，取自 AuthCheckResponse.parsePacket()。
    //   · 命令字：RCSP 的 CmdAuthCheck = 80。
    //
    // 未确认项：应答帧的“命令字”字节取自帧内第 8 字节，与官方 RCSP 的 opCode 是否同源尚未在真机确认。
    // 因此这里不订阅具体命令字，而是按「17 字节负载」的应答形状识别——避免依赖未验证的字段位置假设。
    private async Task<bool> TryAuthenticateAsync(
        ConnectionLink link,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var random = XiaomiAuth.CreateRandomFactor();
        var payload = XiaomiAuth.BuildAuthCheckPayload(random);
        var codec = new XiaomiFrameCodec();
        var completion = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        // RawDataReceived 是按「每次收到字节」触发的，RFCOMM 是流式通道，单帧可能被拆成多次回调，
        // 因此这里自行累积缓冲后再解码（FrameRouter 那条路有重组，但它按命令字订阅，此处用不上）。
        var buffer = new List<byte>();

        void OnRawData(object? sender, ReadOnlyMemory<byte> raw)
        {
            if (completion.Task.IsCompleted)
                return;

            buffer.AddRange(raw.ToArray());
            foreach (var frame in codec.Decode(buffer.ToArray()))
            {
                if (frame.Payload.Length == 1 + XiaomiAuth.AuthRandomLength)
                {
                    completion.TrySetResult(frame);
                    return;
                }
            }

            // 异常流兜底：避免缓冲无上限增长。
            if (buffer.Count > 1024)
                buffer.RemoveRange(0, buffer.Count - 256);
        }

        link.RawDataReceived += OnRawData;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(AuthProbeTimeout);
            await link.SendFireAndForgetAsync(XiaomiConstants.CmdAuthCheck, payload, probe.Token);

            ProtocolFrame frame;
            try
            {
                frame = await completion.Task.WaitAsync(probe.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ApplicationLog.Current?.Info(
                    "Xiaomi",
                    $"认证探测 {AuthProbeTimeout.TotalMilliseconds:F0}ms 内无应答：{deviceName}，按「该机型不要求认证」继续。");
                return false;
            }

            if (!XiaomiAuth.TryParseAuthCheckResponse(frame.Payload.Span, out var versionResponse, out var result))
            {
                ApplicationLog.Current?.Info(
                    "Xiaomi",
                    $"认证应答长度不符（{frame.Payload.Length} 字节），跳过校验：{deviceName}。");
                return false;
            }

            if (XiaomiAuth.VerifyAuthResponse(random, result))
            {
                ApplicationLog.Current?.Info(
                    "Xiaomi",
                    $"认证通过（应答版本 0x{versionResponse:X2}）：{deviceName}。");
                return true;
            }

            // 认证失败不阻断会话：电量等命令可能仍可用，真实不可用的表现会在后续读取上体现。
            ApplicationLog.Current?.Error(
                "Xiaomi",
                $"认证失败：设备应答与本地计算值不符（应答版本 0x{versionResponse:X2}）：{deviceName}。后续命令可能被拒绝。");
            return false;
        }
        catch (Exception ex)
        {
            ApplicationLog.Current?.Error("Xiaomi", $"认证探测异常，已跳过：{ex.Message}", ex);
            return false;
        }
        finally
        {
            link.RawDataReceived -= OnRawData;
        }
    }

    private static byte Clamp(byte value) => value > 100 ? (byte)100 : value;

    public async Task DisconnectAsync()
    {
        if (_link is not null)
        {
            _link.Disconnected -= OnLinkDisconnected;
            await _link.DisposeAsync();
        }

        _link = null;
        _capability = DeviceCapability.Unknown;
        State.Reset();
    }

    public ValueTask DisposeAsync()
    {
        State.Changed -= PublishState;
        return new ValueTask(DisconnectAsync());
    }

    private void PublishState(object? sender, BusinessSnapshot snapshot) => StateChanged?.Invoke(this, snapshot);

    private void OnLinkDisconnected(object? sender, EventArgs args)
    {
        _link = null;
        _capability = DeviceCapability.Unknown;
        State.Reset();
    }

    // ---- 能力未实现的安全兜底：UI 不会触发（Presentation 不显示对应控件），调用亦返回 false ----
    private Task<bool> Unsupported() => Task.FromResult(false);

    // 游戏模式（低延迟）：走 RCSP 配置写通道，configId = 47（LowLatency），值 01=开 / 00=关。
    // 证据：MiBudsClient 的 MODE_COMMAND_TEMPLATE 中 0x002F(=47) 之后为 {param} ∈ {01,00}。
    public async Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        var acked = await _config.WriteAsync(
            _link,
            XiaomiConstants.LowLatency,
            enabled ? (byte)0x01 : (byte)0x00,
            cancellationToken);

        ApplicationLog.Current?.Info("Xiaomi",
            $"设置游戏模式（低延迟）：enabled={enabled}，收到应答={acked}。");

        // 小米写命令是否回 ack 尚未确认，故无论是否收到都按操作值更新 UI ——
        // 否则设备不回 ack 时界面会永远卡在旧值、用户无法继续操作。
        // 若设备实际拒绝，下一次会话的读取会以真实状态覆盖。
        _controlStates["game-mode"] = enabled;
        State.NotifyChanged();
        return acked;
    }

    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken) => Unsupported();

    public bool IsValidCustomEqualizerName(string name) => false;
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => NullEqualizerProfile.Instance.CreateCustomEqualizerEntry(id, name, gains);
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => NullEqualizerProfile.Instance.AlignCustomEqualizerGains(entry);
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => MultiDevicePolicy.BuildDisplayState(State.Snapshot().MultiDevice, hiddenAddresses);
}
