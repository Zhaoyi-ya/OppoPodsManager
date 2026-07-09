using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OppoPodsManager;

/// <summary>
/// 编排层：在 IPodTransport 之上组织命令收发与状态解析。
/// 不关心底层是经典 SPP 还是 BLE GATT——只依赖 IPodTransport。
/// 传输选择由 TransportFactory 决定
/// </summary>
public partial class PodManager : IPodManager
{
    private readonly IPodTransport _transport;
    private bool _disposed;

    public PodState State { get; } = new();
    public DeviceCapabilities Caps { get; private set; } = DeviceCapabilities.Detect(null);

    // 每设备能力集（命令发送前先做能力判定，不支持则拦截）。
    // melody 靠 0x11C 位图；本机 SPP 无该响应，改由型号 JSON(Caps) 推导。
    private readonly HashSet<ushort> _deviceCaps = new();

    // 全局通用能力（所有设备默认支持，无需位图校验）。
    // 含 0x105/0x106/0x107/0x108/0x109（melody Layer1 allowlist）。
    private static readonly HashSet<ushort> GlobalCaps = new()
    {
        OppoProtocol.CmdQueryProductId, OppoProtocol.CmdProductIdResp,
        OppoProtocol.CmdBattery, OppoProtocol.CmdBatteryResp,
        OppoProtocol.CmdActiveReport, OppoProtocol.CmdRegisterNotify,
        OppoProtocol.CmdBatchQuery,
        OppoProtocol.CmdQueryVersion, OppoProtocol.CmdQueryUpgradeCap,
        OppoProtocol.CmdQueryFunctionKey, OppoProtocol.CmdQueryEarStatus,
    };

    public event Action? StateChanged;
    public event Action<string>? CommandFailed;
    public string? LastError => _transport.LastError;
    public bool IsConnected => State.Connected;

    // 跟踪已发送删除命令但设备可能仍短暂回读旧状态的 EQ ID（Melody 式删除追踪窗口）
    private readonly Dictionary<byte, DateTime> _pendingDeleteEqIds = new();

    /// <summary>带超时/重试的设置命令：失败或超时时触发 CommandFailed（供 UI 提示）。</summary>
    private void SendSet(ushort cmd, byte[] payload, string label)
    {
        if (!Supports(cmd))
        {
            Log.D("RFCOMM", $"SendSet: 型号 {Caps.ModelName} 不支持 {label}(0x{cmd:X4})，已忽略");
            return;
        }
        _dispatcher.SendTracked(cmd, payload, (status, _) =>
        {
            if (status != CmdStatus.Success)
            {
                Log.D("RFCOMM", $"SendSet: {label} 失败 status={(int)status}");
                CommandFailed?.Invoke($"{label} 失败" + (status == CmdStatus.Timeout ? "（超时）" : ""));
            }
        });
    }

    /// <summary>
    /// 功能开关发送（空间音效/游戏/双设备）。
    /// 统一用命令 0x403 + [feature][enable]，兼容 BR/EDR 与 LE Audio 传输（同一命令）。
    /// 该命令属通用功能开关，各功能靠 feature 字节区分，不存在新旧命令切换。
    /// </summary>
    private void SendFeatureSwitch(byte feature, bool on, string label)
        => SendSet(OppoProtocol.CmdSetFeature, OppoProtocol.FeaturePayload(feature, on), label);

    // 命令分发器（超时/重试/请求-响应配对）
    private readonly PacketDispatcher _dispatcher;

    public PodManager() : this(TransportFactory.Create()) { }

    /// <summary>连接指定地址的耳机（多耳机切换用）。addr==0 等价于无参构造。</summary>
    public PodManager(ulong targetAddr, string? name) : this(TransportFactory.Create(targetAddr, name)) { }

    public PodManager(IPodTransport transport)
    {
        // 包一层 PacketDispatcher：SendTracked 走 10s 超时/重试/配对；普通 Send 透传
        _dispatcher = new PacketDispatcher(transport);
        _transport = _dispatcher;
        _transport.FrameReceived += DispatchFrame;
        _transport.Disconnected += OnDisconnected;
        RebuildCapabilitySet();
    }

    /// <summary>
    /// 按当前 Caps（型号 JSON）重建每设备能力集。识别到 productId 后能力随之更新。
    /// </summary>
    private void RebuildCapabilitySet()
    {
        _deviceCaps.Clear();
        // ANC：有降噪选项才允许 ANC 查询/设置
        if (Caps.AncOptions.Count > 0 || Caps.AncNameToIndex.Count > 0)
        {
            _deviceCaps.Add(OppoProtocol.CmdQueryAnc);
            _deviceCaps.Add(OppoProtocol.CmdAnc);
            _deviceCaps.Add(OppoProtocol.CmdAncResp);
        }
        // EQ：有预设才允许
        if (Caps.EqPresets.Count > 0)
        {
            _deviceCaps.Add(OppoProtocol.CmdQueryEq);
            _deviceCaps.Add(OppoProtocol.CmdSetEq);
            _deviceCaps.Add(OppoProtocol.CmdEqResp);
            _deviceCaps.Add(OppoProtocol.CmdEqNotify);
            _deviceCaps.Add(OppoProtocol.CmdQueryEqAll);  // 0x0122 设备端 EQ 名称回读
        }
        // 自定义 EQ：设备支持即开放 0x0418（频率兜底在 SendCustomEq 内处理）
        if (Caps.HasCustomEq)
            _deviceCaps.Add(OppoProtocol.CmdSetEqDetail);
        // 空间音频三模式：设置命令 0x0422 + 状态回读查询 0x012A（getHeadsetSpatialType）
        if (Caps.HasSpatialAudio)
        {
            _deviceCaps.Add(OppoProtocol.CmdSpatialAudio);
            _deviceCaps.Add(OppoProtocol.CmdQueryHeadsetSpatial);
            _deviceCaps.Add(OppoProtocol.CmdHeadsetSpatialResp);
        }
        // 双设备连接：多连接列表查询 + 切换活动设备
        if (Caps.HasDualDevice)
        {
            _deviceCaps.Add(OppoProtocol.CmdMultiConnectInfo);
            _deviceCaps.Add(OppoProtocol.CmdMultiConnectResp);
            _deviceCaps.Add(OppoProtocol.CmdOperateHandheld);
        }
        // 多连接优先级/自动切换（MultiDevicesConnect>=2 才支持优先设备管理 + 0x0132 查询）
        if (Caps.HasMultiConnectManage)
        {
            _deviceCaps.Add(OppoProtocol.CmdQueryMultiPriority);
            _deviceCaps.Add(OppoProtocol.CmdMultiPriorityResp);
        }
        // 功能开关（空间音效/游戏/双设备）：用通用命令 0x403
        // ([feature][enable])，兼 BR/EDR 与 LE Audio。
        // 0x423 是"游戏音效类型"等专用功能的独立命令，不是 0x403 的替代路径。
        if (Caps.HasSpatialSound || Caps.HasGameMode || Caps.HasDualDevice)
            _deviceCaps.Add(OppoProtocol.CmdSetFeature);        // 0x403 通用功能开关
        // 游戏音效：设置命令 0x423（setGameSoundTypeEnable）+ 查询命令 0x12B（GameSoundInfo）
        if (Caps.HasGameSound)
        {
            _deviceCaps.Add(OppoProtocol.CmdSetFeatureSwitch);
            _deviceCaps.Add(OppoProtocol.CmdQueryGameSound);
        }
        // 编解码器查询：受支持型号普遍可用（有响应则解析，无响应超时丢弃，无副作用）
        if (Caps.IsSupported)
            _deviceCaps.Add(OppoProtocol.CmdQueryCodecType);
    }

    /// <summary>命令能力判定：全局通用集 或 每设备集 命中才可发。</summary>
    private bool Supports(ushort cmd) => GlobalCaps.Contains(cmd) || _deviceCaps.Contains(cmd);

    /// <summary>该型号是否有"智能切换"档位（有则需额外查询实时档位 [0x03,0x01]）。</summary>
    private bool HasSmartMode() => Caps.AncNameToIndex.ContainsKey("Smart");

    /// <summary>能力门控的发送：不支持则拦截并记日志（"UNSUPPORTED cmd=0x..." 模式）。</summary>
    private bool TrySend(ushort cmd, byte[] payload)
    {
        if (!Supports(cmd))
        {
            Log.D("RFCOMM", $"TrySend: 拦截不支持的命令 cmd=0x{cmd:X4}（当前型号 {Caps.ModelName} 无此能力）");
            return false;
        }
        _transport.Send(cmd, payload);
        return true;
    }

    private void OnDisconnected()
    {
        Log.D("RFCOMM", "OnDisconnected: 传输层报告断开");
        State.Connected = false;
        StateChanged?.Invoke();
    }

    private void DispatchFrame(PodFrame frame)
    {
        var p = frame.Payload;
        int len = p.Length;
        switch (frame.Cmd)
        {
            case OppoProtocol.CmdBatteryResp: ParseBattery(p, 0, len); break;
            case OppoProtocol.CmdAncResp: ParseAnc(p, 0, len); break;
            case OppoProtocol.CmdActiveReport: ParseActiveReport(p, 0, len); break;
            case OppoProtocol.CmdEqResp:
            case OppoProtocol.CmdEqNotify: ParseEq(p, 0, len); break;
            case OppoProtocol.CmdBatchQueryResp: ParseBatchStatus(p, 0, len); break;
            case OppoProtocol.CmdMultiConnectResp: ParseMultiConnect(p, 0, len); break;
            case OppoProtocol.CmdProductIdResp: ParseProductId(p, 0, len); break;
            case OppoProtocol.CmdMultiPriorityResp: ParseMultiPriority(p, 0, len); break;

            // ===== 新增：固件版本 / 编解码器 响应 =====
            case (ushort)(OppoProtocol.CmdQueryVersion | 0x8000):  // 0x8105
            {
                var payload = Slice(p, 0, len);
                var ver = OppoProtocol.ParseVersion(payload);
                if (ver != null) { State.FirmwareVersion = ver; Log.D("RFCOMM", $"固件版本={ver}"); StateChanged?.Invoke(); }
                break;
            }
            case (ushort)(OppoProtocol.CmdQueryCodecType | 0x8000):  // 0x8114
            {
                var payload = Slice(p, 0, len);
                Log.D("RFCOMM", $"编解码查询响应 raw={BitConverter.ToString(payload)}");
                int codec = OppoProtocol.ParseCodecType(payload);
                if (codec >= 0) { State.CodecType = codec; Log.D("RFCOMM", $"编解码器={codec}"); StateChanged?.Invoke(); }
                break;
            }
            case (ushort)(OppoProtocol.CmdQueryEqAll | 0x8000):  // 0x8122 设备端全部 EQ 信息
                Log.D("RFCOMM", $"收到 0x8122, len={len}"); ParseEqAll(p, 0, len); break;
            case OppoProtocol.CmdHeadsetSpatialResp:  // 0x812A 空间音频三模式当前值
            {
                int type = OppoProtocol.ParseHeadsetSpatialType(p);
                if (type >= 0)
                {
                    State.SpatialMode = OppoProtocol.SpatialTypeToName(type);
                    Log.D("RFCOMM", $"空间音频三模式={State.SpatialMode}(type={type})");
                    StateChanged?.Invoke();
                }
                break;
            }
            case (ushort)(OppoProtocol.CmdQueryGameSound | 0x8000):  // 0x812B 游戏音效信息
            {
                // 响应 [status(1)][selectType(1)][count(1)][supportTypes...]。
                // selectType != 0 → 游戏音效已开启（selectType 即当前生效音效 type，如 3）；
                // selectType == 0 → 关闭（gameSoundList 里的 {type:0} 即"关闭"项）。
                if (len >= 2 && p[0] == 0x00)
                {
                    byte selectType = p[1];
                    State.GameSound = selectType != 0;
                    Log.D("RFCOMM", $"游戏音效 selectType={selectType} -> {State.GameSound}");
                    StateChanged?.Invoke();
                }
                break;
            }

            // ===== 通知注册响应族（NotificationCommandManager）=====
            case OppoProtocol.CmdRegisterMultiResp:
                // 批量注册完成 = 初始化握手结束 ACK（置 setInitCmdCompleted）
                Log.D("RFCOMM", "DispatchFrame: 注册通知完成 (0x8205 握手 ACK)");
                break;
            case OppoProtocol.CmdNotifyCapabilityResp:
                Log.D("RFCOMM", $"DispatchFrame: 通知能力响应 (0x8200) len={len}");
                break;
            case OppoProtocol.CmdRegisterNotifyResp:
            case OppoProtocol.CmdCancelNotifyResp:
                Log.D("RFCOMM", $"DispatchFrame: 注册/取消通知响应 cmd=0x{frame.Cmd:X4} status={(len > 0 ? p[0] : -1)}");
                break;
            case OppoProtocol.CmdRegisterNotifyEvent:
                // 注册后携带的事件：跳过 status 字节按通知事件解析
                if (len > 1) ParseActiveReport(p, 1, len - 1);
                break;

            default:
                // SET 命令响应族 0x8400-0x843B：
                // 载荷首字节为状态码(0=成功)。覆盖 SendEq(0x8406)/SendAnc(0x8404)/
                // SendFeature(0x8403)/SendSpatialAudio(0x8422) 等所有写命令 ACK。
                if (frame.Cmd >= 0x8400 && frame.Cmd <= 0x843B)
                {
                    int status = len > 0 ? p[0] : -1;
                    if (status == 0)
                    {
                        Log.D("RFCOMM", $"DispatchFrame: 设置成功 cmd=0x{frame.Cmd:X4}");
                        // 0x8418 = CmdSetEqDetail 的响应：删除/保存 EQ 的 ACK。
                        // 注意：不由这里处理追踪列表和 DeviceEqEntries——交由 ParseEqAll 统一消费。
                        // 见 Melody APK 的 f5125V 删除追踪列表做法（ParseEqAll 才是单一过滤点）。
                        if (frame.Cmd == OppoProtocol.CmdSetEqDetail + 0x8000 && _pendingDeleteEqIds.Count > 0)
                        {
                            Log.D("RFCOMM", $"收到删除 ACK, _pendingDeleteEqIds=[{string.Join(",", _pendingDeleteEqIds.Keys)}] 等待设备刷新");
                        }
                    }
                    else
                        Log.D("RFCOMM", $"DispatchFrame: 设置失败 cmd=0x{frame.Cmd:X4} status={status}");
                    break;
                }
                // RequestCommandManager 状态事件族 0x0500-0x05FF（耳机主动上报：下载/执行/播放
                // 等一次性请求的状态；如 0x501）。仅记日志，无对应 UI，这里静默。
                // 0x0504(EQ 变更通知) 已在上方独立分支处理，不会落到这里。
                if (frame.Cmd >= 0x0500 && frame.Cmd <= 0x05FF)
                {
                    Log.D("RFCOMM", $"DispatchFrame: RCM 状态事件 cmd=0x{frame.Cmd:X4} len={len}");
                    break;
                }
                Log.D("RFCOMM", $"DispatchFrame: 未处理 cmd=0x{frame.Cmd:X4} len={len}");
                break;
        }
    }

    public async Task ConnectAsync()
    {
        await Task.Run(() =>
        {
            Log.D("RFCOMM", "ConnectAsync: 开始连接");
            if (!_transport.Connect())
            {
                Log.D("RFCOMM", "ConnectAsync: 传输层连接失败 -> " + (_transport.LastError ?? "unknown"));
                return;
            }

            Caps = DeviceCapabilities.Detect(_transport.DeviceName);  // 名称预判（回退）
            RebuildCapabilitySet();
            State.Connected = true;
            Log.D("RFCOMM", $"ConnectAsync: 连接成功,开始握手序列 (名称预判 Caps={Caps.ModelName})");

            // ===== 阶段1：设备识别（先取 productId 精确定位能力）=====
            // productId 属全局通用能力，先发；收到 0x8103 后 ParseProductId 会重建能力集。
            _transport.Send(OppoProtocol.CmdQueryProductId, OppoProtocol.PayEmpty);
            Thread.Sleep(120);
            _transport.Poll(600);   // 先把 productId 响应收进来，让后续查询基于正确能力集

            // ===== 阶段2：批量功能状态（游戏/双设备/空间音效开关）=====
            _transport.Send(OppoProtocol.CmdBatchQuery, OppoProtocol.PayBatchQuery);
            Thread.Sleep(80);

            // ===== 阶段3：能力门控的状态查询（不支持的型号自动跳过）=====
            _transport.Send(OppoProtocol.CmdBattery, OppoProtocol.PayEmpty);   // 全局
            Thread.Sleep(80);
            _transport.Send(OppoProtocol.CmdQueryVersion, OppoProtocol.PayEmpty);  // 固件版本（全局）
            Thread.Sleep(80);
            TrySend(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAnc);
            Thread.Sleep(80);
            if (HasSmartMode()) { TrySend(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAncIntelligent); Thread.Sleep(80); }
            TrySend(OppoProtocol.CmdQueryEq, OppoProtocol.PayEmpty);
            Thread.Sleep(80);
            _transport.Send(OppoProtocol.CmdQueryEqAll, OppoProtocol.PayQueryEqAll);  // 设备端 EQ 名称回读（不受能力门控）
            Thread.Sleep(80);
            TrySend(OppoProtocol.CmdQueryCodecType, OppoProtocol.PayEmpty);
            Thread.Sleep(80);
            TrySend(OppoProtocol.CmdQueryGameSound, OppoProtocol.PayEmpty);  // 游戏音效当前状态
            Thread.Sleep(80);
            // 空间音频三模式当前值：仅三模式（headsetSpatialType）机型才有 0x012A，
            // 两模式开关型机型走 feature 0x1B（批量查询）已覆盖，不发以免无谓拦截日志。
            if (Caps.HasSpatialAudio)
            {
                TrySend(OppoProtocol.CmdQueryHeadsetSpatial, OppoProtocol.PayEmpty);
                Thread.Sleep(80);
            }

            // ===== 阶段4：注册主动通知（电池/佩戴）=====
            _transport.Send(OppoProtocol.CmdRegisterNotify, OppoProtocol.PayRegisterNotify);
            Thread.Sleep(80);
            _transport.Send(OppoProtocol.CmdRegisterNotify, OppoProtocol.PayRegisterWear);
            Thread.Sleep(80);

            // ===== 阶段5：多设备列表（仅双设备型号）=====
            TrySend(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty);
            Thread.Sleep(400);

            Log.D("RFCOMM", "ConnectAsync: 握手命令已发完,等待首批响应");
            _transport.Poll(3000);
            Log.D("RFCOMM", "ConnectAsync: 初始化完成");
        });
    }

    public void SendMultiConnectInfo()
    {
        Log.D("RFCOMM", "SendMultiConnectInfo");
        TrySend(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty);
    }

    /// <summary>
    /// 多设备操作统一入口。用 SendSet（带 ACK 追踪/重试）而非裸 Send：
    /// - 设备回 0x8429 status=0 → 成功；status!=0 → CommandFailed 提示；超时 → 重试后报超时。
    /// - 打印完整载荷字节，便于据日志确认设备是"拒绝(NAK)""无响应"还是"格式不符被忽略"。
    /// 原实现用裸 TrySend(fire-and-forget)，设备丢包或拒绝都无声无息，正是"点断开没反应"的元凶之一。
    /// </summary>
    private void SendMultiConnectOp(byte operateType, string targetAddress, string label, bool clearAddress = false)
    {
        var payload = OppoProtocol.MultiConnectOpPayload(operateType, targetAddress, clearAddress);
        Log.D("RFCOMM", $"多设备操作: {label} addr={targetAddress} type={operateType} payload=[{BitConverter.ToString(payload)}]");
        SendSet(OppoProtocol.CmdOperateHandheld, payload, $"多设备{label}");
    }

    public void SendMultiConnectConnect(string targetAddress) =>
        SendMultiConnectOp(OppoProtocol.MultiOpConnect, targetAddress, "连接");

    public void SendMultiConnectDisconnect(string targetAddress) =>
        SendMultiConnectOp(OppoProtocol.MultiOpDisconnect, targetAddress, "断开");

    public void SendMultiConnectSetPriority(string targetAddress) =>
        SendMultiConnectOp(OppoProtocol.MultiOpSetPriority, targetAddress, "设为优先");

    public void SendMultiConnectUnpair(string targetAddress) =>
        SendMultiConnectOp(OppoProtocol.MultiOpUnpair, targetAddress, "取消配对");

    public void SendOperateHandheld(string targetAddress, bool connect = true)
    {
        if (connect) SendMultiConnectConnect(targetAddress);
        else SendMultiConnectDisconnect(targetAddress);
    }

    public void SendAnc(string mode)
    {
        Log.D("RFCOMM", $"SendAnc mode={mode}");
        if (!Supports(OppoProtocol.CmdAnc))
        {
            Log.D("RFCOMM", $"SendAnc: 型号 {Caps.ModelName} 无降噪能力，已忽略");
            return;
        }
        byte[] payload;
        if (Caps.AncNameToIndex.TryGetValue(mode, out var idx))
            payload = OppoProtocol.AncPayloadByIndex(idx);
        else
        {
            var m = Caps.IsLegacyAnc ? OppoProtocol.LegacyAncSwap(mode) : mode;
            payload = OppoProtocol.AncPayloadByName(m);
        }
        SendSet(OppoProtocol.CmdAnc, payload, $"降噪设置 {mode}");
    }

    public void SendBattery()
    {
        Log.D("RFCOMM", "SendBattery");
        _transport.Send(OppoProtocol.CmdBattery, OppoProtocol.PayEmpty);  // 全局能力
    }

    public void SendSpatial(bool on)
    {
        Log.D("RFCOMM", $"SendSpatial on={on}");
        SendFeatureSwitch(OppoProtocol.FeatureSpatial, on, "空间音效");
    }

    public void SendSpatialAudio(string mode)
    {
        Log.D("RFCOMM", $"SendSpatialAudio mode={mode}");
        // 空间音频三模式是独立命令 0x0422（非功能开关），保持原路径
        SendSet(OppoProtocol.CmdSpatialAudio, OppoProtocol.SpatialPayload(mode), $"空间音频 {mode}");
    }

    public void SendDualDevice(bool on)
    {
        Log.D("RFCOMM", $"SendDualDevice on={on}");
        SendFeatureSwitch(OppoProtocol.FeatureDualDevice, on, "双设备连接");
    }

    public void SendGameMode(bool on, bool compatible = false)
    {
        Log.D("RFCOMM", $"SendGameMode on={on} compatible={compatible}");
        SendFeatureSwitch(OppoProtocol.FeatureGameMain, on, "游戏模式");
        // 兼容实现：部分设备游戏低延迟需额外发 feature 0x06
        if (compatible)
            SendFeatureSwitch(OppoProtocol.FeatureGameLL, on, "游戏低延迟");
    }

    /// <summary>
    /// 游戏音效开关（命令 0x423 + [type][enable]）。
    /// "关闭"不是发 enable=0，而是选择 type 0（gameSoundList 里的 {type:0} 即"关闭"项），
    /// 且 enable 恒为 1。
    /// 开关状态由设备回读的 selectType 决定：selectType != 0 = 开、== 0 = 关。
    /// 故：开 → [GameSoundType, 1]；关 → [0, 1]。若发 [GameSoundType, 0]，设备 selectType 仍保留原 type，
    /// 会被回读判为"仍开启"（本次 bug 根因）。
    /// </summary>
    public void SendGameSound(bool on)
    {
        byte type = on ? Caps.GameSoundType : (byte)0x00;
        Log.D("RFCOMM", $"SendGameSound on={on} type={type}");
        SendSet(OppoProtocol.CmdSetFeatureSwitch,
                OppoProtocol.FeatureSwitchPayload(type, true), "游戏音效");
    }

    /// <summary>
    /// 音效增强互斥组当前生效项（游戏音效 ↔ 调音 ↔ 空间音效互斥）。
    /// 从设备回读状态推导；无互斥组或都关时为 None。
    /// </summary>
    public AudioEnhancement CurrentEnhancement()
    {
        if (State.GameSound) return AudioEnhancement.GameSound;
        if (Caps.GameSoundMutexSpatial && State.SpatialSound) return AudioEnhancement.SpatialSound;
        // EQ 视为"调音"生效：有非默认预设即算（EQ 总有值，故仅在与游戏音效互斥的型号上作为一员）
        if (Caps.GameSoundMutexEq && State.EqPreset is { } preset
            && preset != "?" && preset != Caps.DefaultEqPreset)
            return AudioEnhancement.Eq;
        return AudioEnhancement.None;
    }

    /// <summary>
    /// 设置音效增强（互斥组单选，静默切换）。选一项 → 只发该项 enable，
    /// 设备固件自动关掉互斥的其它项（不重复下发关闭命令）。
    /// mode=None 时关闭游戏音效（其它项本就是"开一个关其它"，无独立关闭语义）。
    /// </summary>
    public void SetAudioEnhancement(AudioEnhancement mode, string? eqName = null)
    {
        Log.D("RFCOMM", $"SetAudioEnhancement -> {mode} eq={eqName}");
        _featureUserSetAtProxy();
        switch (mode)
        {
            case AudioEnhancement.GameSound:
                SendGameSound(true);
                break;
            case AudioEnhancement.SpatialSound:
                // 开空间音效开关；设备会关游戏音效（互斥）
                SendSpatial(true);
                break;
            case AudioEnhancement.Eq:
                // 选调音 = 应用某个 EQ 预设；设备会关游戏音效（互斥）
                if (!string.IsNullOrEmpty(eqName)) SendEq(eqName!);
                break;
            case AudioEnhancement.None:
                // 关闭：显式关游戏音效（EQ/空间音效无"全关"单命令，交给各自控件）
                if (State.GameSound) SendGameSound(false);
                break;
        }
    }

    // 供 SetAudioEnhancement 复用 UI 的"用户刚操作"时间戳抑制回读覆盖（由构造注入或空实现）
    private Action _featureUserSetAtProxy = () => { };
    /// <summary>UI 可注入：标记"刚由用户设置"，避免轮询回读在短时间内覆盖选择。</summary>
    public void SetFeatureUserSetHook(Action hook) => _featureUserSetAtProxy = hook ?? (() => { });

    public void SendEq(string name)
    {
        if (!Supports(OppoProtocol.CmdSetEq))
        {
            Log.D("RFCOMM", $"SendEq: 型号 {Caps.ModelName} 无 EQ 能力，已忽略");
            return;
        }
        // 先查 JSON 内置预设，再查设备端 EQ 条目
        byte eqId;
        if (Caps.EqPresets.TryGetValue(name, out var id))
        {
            eqId = id;
        }
        else if (State.DeviceEqEntries.FirstOrDefault(ev => ev.Name == name) is { } devEntry)
        {
            eqId = devEntry.EqId;
        }
        else
        {
            Log.D("RFCOMM", $"SendEq: 未知 EQ 预设 \"{name}\",已忽略");
            return;
        }
        Log.D("RFCOMM", $"SendEq name={name} id={eqId}");
        SendSet(OppoProtocol.CmdSetEq, new byte[] { eqId }, $"EQ {name}");
    }

    /// <summary>查询设备端全部 EQ 信息（cmd 0x0122）。结果解析为 <see cref="PodState.DeviceEqEntries"/> + 名称回填 <see cref="DeviceCapabilities.DeviceEqNames"/>。</summary>
    public void SendQueryEqAll()
    {
        Log.D("RFCOMM", "SendQueryEqAll");
        _transport.Send(OppoProtocol.CmdQueryEqAll, OppoProtocol.PayQueryEqAll);
    }

    private int[] ResolveCustomEqFreqs()
    {
        var freqs = Caps.CustomEqFrequencies;
        return freqs.Length == 0 ? [62, 250, 1000, 4000, 8000, 16000] : freqs;
    }

    /// <summary>新建自定义 EQ 预设（cmd 0x0418, actionType=1）。仅创建，不用于更新已有预设。
    /// 对应 Melody 的 B8.c.i(addr, eqInfo, 1)。发送后查询列表以取得设备分配的新 eqId。</summary>
    public void SendCustomEq(int[] gains, string name)
    {
        if (!Supports(OppoProtocol.CmdSetEqDetail))
        {
            Log.D("RFCOMM", $"SendCustomEq(name): 型号 {Caps.ModelName} 无自定义 EQ 能力，已忽略");
            return;
        }
        var freqs = ResolveCustomEqFreqs();
        var payload = OppoProtocol.EqDetailPayload(gains, freqs, name);
        Log.D("RFCOMM", $"SendCustomEq(新建) name={name} gains=[{string.Join(",", gains)}] freqs=[{string.Join(",", freqs)}] payload=[{string.Join(",", payload)}]");
        SendSet(OppoProtocol.CmdSetEqDetail, payload, $"新建 EQ {name}");
        SendQueryEqAll();
    }

    /// <summary>更新/应用已有 EQ 预设（cmd 0x0418, actionType=2）。带原 eqId + 完整 EqInfo，避免设备当新建。
    /// 对应 Melody 的 B8.c.i(addr, eqInfo, 2)：编辑滑块预览与切换选中都走此 action。</summary>
    public void UpdateCustomEq(byte eqId, int[] gains, string name, int minValue = -6, int maxValue = 6)
    {
        if (!Supports(OppoProtocol.CmdSetEqDetail))
        {
            Log.D("RFCOMM", $"UpdateCustomEq: 型号 {Caps.ModelName} 无自定义 EQ 能力，已忽略");
            return;
        }
        var freqs = ResolveCustomEqFreqs();
        var entry = new EqInfoEntry
        {
            EqId = eqId,
            Name = name,
            Frequencies = freqs,
            Gains = gains,
            MinValue = minValue,
            MaxValue = maxValue,
        };
        var payload = OppoProtocol.EqUpdatePayload(entry);
        Log.D("RFCOMM", $"UpdateCustomEq(更新) eqId={eqId} name={name} gains=[{string.Join(",", gains)}] freqs=[{string.Join(",", freqs)}] payload=[{string.Join(",", payload)}]");
        SendSet(OppoProtocol.CmdSetEqDetail, payload, $"更新 EQ {name}");
    }

    /// <summary>删除设备端 EQ 预设（cmd 0x0418, actionType=3）。发送删除命令后立即查询全量列表以刷新。</summary>
    public void DeleteEq(int eqId)
    {
        if (!Supports(OppoProtocol.CmdSetEqDetail))
        {
            Log.D("RFCOMM", $"DeleteEq: 型号 {Caps.ModelName} 无自定义 EQ 能力，已忽略");
            return;
        }
        var entry = State.DeviceEqEntries.FirstOrDefault(e => e.EqId == eqId);
        var payload = entry != null
            ? OppoProtocol.EqDeletePayload(entry)
            : OppoProtocol.EqDeletePayload(eqId);
        Log.D("RFCOMM", $"DeleteEq eqId={eqId} fullEntry={entry != null} payload=[{string.Join(",", payload)}]");
        _pendingDeleteEqIds[(byte)eqId] = DateTime.Now.AddSeconds(1);
        SendSet(OppoProtocol.CmdSetEqDetail, payload, "删除 EQ");
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            SendQueryEqAll();
        });
    }

    public async Task PollAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            int tick = 0;
            int intervalMs = 2000;
            while (!ct.IsCancellationRequested && State.Connected)
            {
                try
                {
                    // 高频：电池（全局）+ ANC（能力门控）
                    _transport.Send(OppoProtocol.CmdBattery, OppoProtocol.PayEmpty);
                    Thread.Sleep(100);
                    TrySend(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAnc);
                    Thread.Sleep(100);
                    // 智能切换档位需单独查询（[0x03,0x01]），拿设备实时计算档位
                    if (HasSmartMode())
                    {
                        TrySend(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAncIntelligent);
                        Thread.Sleep(100);
                    }
                    _transport.Poll(500);
            _transport.Send(OppoProtocol.CmdRegisterNotify, OppoProtocol.PayRegisterWear);
                    tick++;

                    // 低频：EQ（能力门控）
                    if (tick % 6 == 0 && TrySend(OppoProtocol.CmdQueryEq, OppoProtocol.PayEmpty))
                    {
                        Thread.Sleep(100);
                        _transport.Poll(400);
                    }

                    // 低频：功能开关状态 + 重注册通知 + 多设备列表
                    if (tick % 4 == 0)
                    {
                        _transport.Send(OppoProtocol.CmdBatchQuery, OppoProtocol.PayBatchQuery);
                        Thread.Sleep(100);
                        _transport.Poll(400);

                        if (TrySend(OppoProtocol.CmdQueryGameSound, OppoProtocol.PayEmpty))
                        {
                            Thread.Sleep(100);
                            _transport.Poll(400);
                        }

                        if (Caps.HasSpatialAudio && TrySend(OppoProtocol.CmdQueryHeadsetSpatial, OppoProtocol.PayEmpty))
                        {
                            Thread.Sleep(100);
                            _transport.Poll(400);
                        }

                        _transport.Send(OppoProtocol.CmdRegisterNotify, OppoProtocol.PayRegisterNotify);
                        Thread.Sleep(100);
                        Thread.Sleep(100);
                        _transport.Poll(400);

                        if (TrySend(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty))
                        {
                            Thread.Sleep(100);
                            _transport.Poll(400);
                        }

                        // 多连接优先设备/自动切换（0x0132）
                        if (TrySend(OppoProtocol.CmdQueryMultiPriority, OppoProtocol.PayEmpty))
                        {
                            Thread.Sleep(100);
                            _transport.Poll(400);
                        }
                    }

                    if (tick == 10) intervalMs = 5000;
                    Thread.Sleep(intervalMs);
                }
                catch (Exception ex)
                {
                    Log.Ex("RFCOMM", "PollAsync 循环中断", ex);
                    break;
                }
            }
            Log.D("RFCOMM", $"PollAsync: 退出轮询循环 (cancelled={ct.IsCancellationRequested}, connected={State.Connected})");
            State.Connected = false;
            _transport.Close();
            StateChanged?.Invoke();
        }, ct);
    }

    public void Disconnect()
    {
        State.Connected = false;
        _transport.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        _transport.Dispose();
        _disposed = true;
    }
}
