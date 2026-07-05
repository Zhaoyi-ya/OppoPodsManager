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
public class PodManager : IPodManager
{
    private readonly IPodTransport _transport;
    private bool _disposed;

    public PodState State { get; } = new();
    public DeviceCapabilities Caps { get; private set; } = DeviceCapabilities.Detect(null);

    // 每设备能力集（对齐官方 ProtocolManager：命令发送前先做能力判定，不支持则拦截）。
    // 官方靠 0x11C 位图；本机 SPP 无该响应，改由型号 JSON(Caps) 推导，语义等价。
    private readonly HashSet<ushort> _deviceCaps = new();

    // 全局通用能力（官方 Le7.a.b：所有设备默认支持，无需位图校验）。
    // 含 0x105/0x106/0x107/0x108/0x109（官方 Layer1 allowlist）。
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
    /// 功能开关发送（空间音效/游戏/双设备）。官方 HeadsetCoreService.a1：
    /// 统一用命令 0x403 + [feature][enable]，兼容 BR/EDR 与 LE Audio 传输（同一命令）。
    /// 该命令属通用功能开关，各功能靠 feature 字节区分，不存在新旧命令切换。
    /// </summary>
    private void SendFeatureSwitch(byte feature, bool on, string label)
        => SendSet(OppoProtocol.CmdSetFeature, OppoProtocol.FeaturePayload(feature, on), label);

    // 命令分发器（官方式超时/重试/请求-响应配对，对齐 PacketTimeoutProcessor）
    private readonly PacketDispatcher _dispatcher;

    public PodManager() : this(TransportFactory.Create()) { }

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
    /// 按当前 Caps（型号 JSON）重建每设备能力集。对齐官方：识别到 productId 后能力随之更新。
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
        }
        // 空间音频三模式：设置命令 0x0422 + 状态回读查询 0x012A（官方 getHeadsetSpatialType）
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
        // 功能开关（空间音效/游戏/双设备）：官方 HeadsetCoreService.a1 用通用命令 0x403
        // ([feature][enable])，兼 BR/EDR 与 LE Audio（0x1f 只切日志标签，命令不变）。
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

    /// <summary>命令能力判定（官方 ProtocolManager.c）：全局通用集 或 每设备集 命中才可发。</summary>
    private bool Supports(ushort cmd) => GlobalCaps.Contains(cmd) || _deviceCaps.Contains(cmd);

    /// <summary>该型号是否有"智能切换"档位（有则需额外查询实时档位 [0x03,0x01]）。</summary>
    private bool HasSmartMode() => Caps.AncNameToIndex.ContainsKey("Smart");

    /// <summary>能力门控的发送：不支持则拦截并记日志（对齐官方"UNSUPPORTED cmd=0x..."）。</summary>
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
                int codec = OppoProtocol.ParseCodecType(payload);
                if (codec >= 0) { State.CodecType = codec; Log.D("RFCOMM", $"编解码器={codec}"); StateChanged?.Invoke(); }
                break;
            }
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
                // 官方一致语义（HearingOptimizeItem.isSelectGameSound / GameSetFragment）：
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

            // ===== 通知注册响应族（官方 NotificationCommandManager.c）=====
            case OppoProtocol.CmdRegisterMultiResp:
                // 批量注册完成 = 初始化握手结束 ACK（官方置 setInitCmdCompleted(true)）
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
                // 注册后携带的事件：官方从 offset+1 递归分发，这里跳过 status 字节按通知事件解析
                if (len > 1) ParseActiveReport(p, 1, len - 1);
                break;

            default:
                // SET 命令响应族 0x8400-0x843B（官方 SetCommandManager.a 统一处理）：
                // 载荷首字节为状态码(0=成功)。覆盖 SendEq(0x8406)/SendAnc(0x8404)/
                // SendFeature(0x8403)/SendSpatialAudio(0x8422) 等所有写命令 ACK。
                if (frame.Cmd >= 0x8400 && frame.Cmd <= 0x843B)
                {
                    int status = len > 0 ? p[0] : -1;
                    if (status == 0)
                        Log.D("RFCOMM", $"DispatchFrame: 设置成功 cmd=0x{frame.Cmd:X4}");
                    else
                        Log.D("RFCOMM", $"DispatchFrame: 设置失败 cmd=0x{frame.Cmd:X4} status={status}");
                    break;
                }
                // RequestCommandManager 状态事件族 0x0500-0x05FF（耳机主动上报：下载/执行/播放
                // 等一次性请求的状态；如 0x501）。官方仅记日志 + 内部 Handler，无对应 UI，这里静默。
                // 注意：0x0504(EQ 变更通知) 已在上方独立分支处理，不会落到这里。
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

            // ===== 阶段1：设备识别（官方先取 productId 精确定位能力）=====
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

    public void SendOperateHandheld(string targetAddress, bool connect = true)
    {
        Log.D("RFCOMM", $"SendOperateHandheld addr={targetAddress} connect={connect}");
        TrySend(OppoProtocol.CmdOperateHandheld, OppoProtocol.OperateHandheldPayload(targetAddress, connect));
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
    /// 游戏音效开关（官方 setGameSoundTypeEnable，命令 0x423 + [type][enable]）。
    /// 关键：官方"关闭"不是发 enable=0，而是【选择 type 0】（gameSoundList 里的 {type:0} 即"关闭"项），
    /// 且 enable 恒为 1（GameSetViewModel.h → E8/a.d(addr, type, true)）。
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
    /// 音效增强互斥组当前生效项（对齐官方 GameSoundMutexHelper：游戏音效 ↔ 调音 ↔ 空间音效互斥）。
    /// 从设备回读状态推导；无互斥组或都关时为 None。
    /// </summary>
    public AudioEnhancement CurrentEnhancement()
    {
        if (State.GameSound) return AudioEnhancement.GameSound;
        if (Caps.GameSoundMutexSpatial && State.SpatialSound) return AudioEnhancement.SpatialSound;
        // EQ 视为"调音"生效：有非默认预设即算（EQ 总有值，故仅在与游戏音效互斥的型号上作为一员）
        return AudioEnhancement.None;
    }

    /// <summary>
    /// 设置音效增强（互斥组单选，静默切换）。选一项 → 只发该项 enable，
    /// 设备固件自动关掉互斥的其它项（对齐官方，不重复下发关闭命令）。
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
        if (Caps.EqPresets.TryGetValue(name, out var id))
        {
            Log.D("RFCOMM", $"SendEq name={name} id={id}");
            SendSet(OppoProtocol.CmdSetEq, new byte[] { id }, $"EQ {name}");
        }
        else
        {
            Log.D("RFCOMM", $"SendEq: 未知 EQ 预设 \"{name}\",已忽略");
        }
    }


    /// <summary>
    /// 官方设备识别：0x8103 响应里拿到 productId 后，用它精确重定能力。
    /// 这是优先路径；名称匹配（ConnectAsync 里的 Detect）只作连接前预判/回退。
    /// </summary>
    private void ParseProductId(byte[] pkt, int start, int len)
    {
        var payload = new byte[len];
        Array.Copy(pkt, start, payload, 0, len);
        var productId = OppoProtocol.ParseProductId(payload);
        if (productId == null) return;

        Log.D("RFCOMM", $"ParseProductId: productId={productId}");
        var byId = DeviceCapabilities.DetectById(productId, _transport.DeviceName);
        if (byId != null)
        {
            Log.D("RFCOMM", $"ParseProductId: 精确识别为 {byId.ModelName}");
            Caps = byId;
            RebuildCapabilitySet();   // 官方：能力随精确识别更新，后续命令按新能力集门控
            StateChanged?.Invoke();
        }
    }

    /// <summary>从帧载荷截取一段（DispatchFrame 里 start 恒为 0，这里统一成独立数组给解析器）。</summary>
    private static byte[] Slice(byte[] pkt, int start, int len)
    {
        if (len <= 0) return Array.Empty<byte>();
        var b = new byte[len];
        Array.Copy(pkt, start, b, 0, len);
        return b;
    }

    private void ParseBattery(byte[] pkt, int start, int len)
    {
        for (int i = 0; i + 1 < len; i += 2)
        {
            int idx = pkt[start + i];
            int raw = pkt[start + i + 1];
            int level = raw & 0x7F;
            bool charging = (raw & 0x80) != 0;
            var key = idx switch { 1 => "L", 2 => "R", 3 => "C", _ => null };
            if (key != null) State.Battery[key] = (level, charging);
        }
        StateChanged?.Invoke();
    }

    private void ParseAnc(byte[] pkt, int start, int len)
    {
        // 0x810C 响应格式：[byte0][subType][mType][bitmap...]。subType=data[1]（官方 o.D 分发）：
        //   1 = CurrentNoiseModeInfo（手动档位，msg 0x17）
        //   2/3 = NoiseReductionInfo（旧版，msg 0x14/0x1f）
        //   4 = IntelligentNoiseModeInfo（智能实时档位，msg 0x2c）← 由 [0x04,0x01] 查询触发
        // 位图从 mType(data[2]) 起，首个置位 bit = 设备实时算出的档位；位图全 0 表示当前无实时档位。
        if (len >= 5 && pkt[start + 1] == 0x04)
        {
            var rt = ParseNoiseBitmap(pkt, start + 2, len - 2);   // 从 mType 起
            // 有实时档位（如深度/中度/轻度）才记；空位图表示设备暂未算出，清空提示。
            State.IntelligentRealtime = (rt != null && rt != "Smart") ? rt : "";
            Log.D("RFCOMM", $"ParseAnc: 智能实时(查询) -> {(string.IsNullOrEmpty(State.IntelligentRealtime) ? "(无)" : State.IntelligentRealtime)}");
            StateChanged?.Invoke();
            return;   // 主档位由手动查询(subType=1)响应设置，这里只管实时档位
        }

        for (int i = 0; i + 3 < len; i++)
        {
            if (pkt[start + i] == 0x01 && pkt[start + i + 1] == 0x01)
            {
                byte v1 = pkt[start + i + 2], v2 = pkt[start + i + 3];

                // 优先：按型号 AncIndexToName，把位图里置位的 bit -> protocolIndex -> 模式名
                if (Caps.AncIndexToName.Count > 0)
                {
                    int value = v1 + v2 * 256;
                    int bit = 1;
                    for (int idx = 0; idx < 16; idx++)
                    {
                        if ((value & bit) != 0 && Caps.AncIndexToName.TryGetValue((byte)idx, out var name))
                        {
                            State.AncMode = name;
                            // 非智能档位时清除智能实时提示（智能实时值只在 Smart 模式有意义）
                            if (name != "Smart") State.IntelligentRealtime = "";
                            break;
                        }
                        bit *= 2;
                    }
                }
                // 回退：静态表（含旧版值交换）
                else if (OppoProtocol.AncValues.TryGetValue((v1, v2), out var mode))
                {
                    State.AncMode = Caps.IsLegacyAnc ? OppoProtocol.LegacyAncSwap(mode) : mode;
                }
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 解析 0x0204 主动通知事件。start 指向子类型字节（payload[0]），
    /// 分发逻辑对齐官方 NotificationCommandManager.b()：payload[start]=subType，事件体从 start+1 起。
    /// </summary>
    private void ParseActiveReport(byte[] pkt, int start, int len)
    {
        if (len < 1) return;
        int subType = pkt[start];
        int bodyStart = start + 1;          // 事件体起点（官方 nextOffset = offset+1）
        int bodyLen = len - 1;
        Log.D("RFCOMM", $"ParseActiveReport: subType=0x{subType:X2}({OppoProtocol.ActiveReportName(subType)}) len={len}");

        switch (subType)
        {
            case OppoProtocol.EvtBattery:        // 0x01 电池 List<BatteryInfo>：[n][deviceType,level+charging]×n
                ParseBatteryList(pkt, bodyStart, bodyLen);
                break;
            case OppoProtocol.EvtEarBudsStatus:  // 0x02 佩戴/入耳状态：[n][comp,status]×n
                ParseWearingData(pkt, start, len);
                break;
            case OppoProtocol.EvtNoiseMode:      // 0x03 降噪变更（次字节区分旧/新/智能）
                ParseNoiseChange(pkt, bodyStart, bodyLen);
                break;
            case OppoProtocol.EvtGameMode:       // 0x05 游戏模式开关
                if (bodyLen >= 1)
                {
                    State.GameMode = pkt[bodyStart] != 0;
                    Log.D("RFCOMM", $"ParseActiveReport: 游戏模式 -> {State.GameMode}");
                }
                break;
            case OppoProtocol.EvtZenMode:        // 0x0A 禅模式开关
                if (bodyLen >= 1)
                    Log.D("RFCOMM", $"ParseActiveReport: 禅模式 -> {pkt[bodyStart]}");
                break;
            case OppoProtocol.EvtMultiConnect:   // 0x06 多设备连接状态：主动触发一次列表刷新
                Log.D("RFCOMM", "ParseActiveReport: 多连接状态变更，刷新列表");
                _transport.Send(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty);
                break;
            case OppoProtocol.EvtCompactness:    // 0x04 贴合检测
            case OppoProtocol.EvtHearingDetect:  // 0x08 听力检测
            case OppoProtocol.EvtCodecType:      // 0x09 编解码
            case OppoProtocol.EvtPersonalNoise:  // 0x0B 个性化降噪
            case OppoProtocol.EvtTriangle:       // 0x0D 空间音频三角
            case OppoProtocol.EvtEarScan:        // 0x0E 耳道扫描
            case OppoProtocol.EvtGaming:         // 0x0F 公共事件
            case OppoProtocol.EvtOneshot:        // 0x10 Oneshot
            case OppoProtocol.EvtToneChange:     // 0x11 耳音调
                // 已识别但当前 UI 未使用，仅记录（避免"未处理"刷屏）
                break;
            default:
                Log.D("RFCOMM", $"ParseActiveReport: 未识别子类型 0x{subType:X2}");
                break;
        }
        StateChanged?.Invoke();
    }

    /// <summary>解析电池列表事件体：[count][deviceType,level+charging]×count（官方 CommandUtil.d + BatteryInfo）。</summary>
    private void ParseBatteryList(byte[] pkt, int start, int len)
    {
        if (len < 1) return;
        int count = pkt[start];
        for (int j = 0; j < count && start + 1 + j * 2 + 1 < start + len; j++)
        {
            int idx = pkt[start + 1 + j * 2];
            int raw = pkt[start + 1 + j * 2 + 1];
            int level = raw & 0x7F;
            bool charging = (raw & 0x80) != 0;
            var key = idx switch { 1 => "L", 2 => "R", 3 => "C", _ => null };
            if (key != null) State.Battery[key] = (level, charging);
        }
    }

    /// <summary>
    /// 解析降噪变更通知（0x0204 子类型 0x03）。事件体首字节为区分符（官方 NCM.b case 0x3）：
    ///   2 = 旧版 NoiseReductionInfo
    ///   1 = 新版 CurrentNoiseModeInfo（[mType][bitmap]，手动档位）
    ///   4 = IntelligentNoiseModeInfo（智能切换：位图里置位的 bit = 设备实时计算出的当前档位）
    /// 位图格式三者一致：置位的 bit → protocolIndex → 按型号 AncIndexToName 得模式名。
    /// </summary>
    private void ParseNoiseChange(byte[] pkt, int start, int len)
    {
        if (len < 1) return;
        int kind = pkt[start];
        int infoStart = start + 1;
        int infoLen = len - 1;

        if (kind == 1)  // CurrentNoiseModeInfo：手动选中的档位
        {
            var name = ParseNoiseBitmap(pkt, infoStart, infoLen);
            if (name != null)
            {
                State.AncMode = name;
                State.IntelligentRealtime = "";  // 手动档位，清除智能实时提示
                Log.D("RFCOMM", $"ParseNoiseChange: 手动 ANC -> {name}");
            }
        }
        else if (kind == 4)  // IntelligentNoiseModeInfo：智能切换，位图首个置位 bit = 实时档位
        {
            var name = ParseNoiseBitmap(pkt, infoStart, infoLen);
            if (name != null)
            {
                State.AncMode = "Smart";              // 当前主档位是"智能切换"
                State.IntelligentRealtime = name;     // 设备实时算出的档位（深度/中度/轻度）
                Log.D("RFCOMM", $"ParseNoiseChange: 智能实时 -> {name}");
            }
        }
        else  // 旧版(2) 或未知：型号差异大，触发一次主动查询由 ParseAnc 统一处理
        {
            _transport.Send(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAnc);
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 解析降噪位图（[mType][bitmap...]）。mType=1(位图) 时取首个置位 bit → protocolIndex → 模式名。
    /// 对齐官方 CurrentNoiseModeInfo / IntelligentNoiseModeInfo.getCurrentNoiseReductionModeIndex()。
    /// 返回模式名，无法解析返回 null。
    /// </summary>
    private string? ParseNoiseBitmap(byte[] pkt, int start, int len)
    {
        if (len < 2 || Caps.AncIndexToName.Count == 0) return null;
        int mType = pkt[start];
        if (mType != 1) return null;  // mType=2 是等级模式，这里只处理位图档位

        // 位图从 start+1 起，低位在前；首个置位 bit 即当前档位（官方取 index 0 起第一个 true）
        int value = 0;
        for (int b = 0; start + 1 + b < start + len && b < 4; b++)
            value |= (pkt[start + 1 + b] & 0xFF) << (b * 8);
        int bit = 1;
        for (int i = 0; i < 32; i++)
        {
            if ((value & bit) != 0 && Caps.AncIndexToName.TryGetValue((byte)i, out var name))
                return name;
            bit *= 2;
        }
        return null;
    }

    private void ParseWearingData(byte[] pkt, int start, int len)
    {
        if (len < 3) return;
        int count = pkt[start + 1];
        for (int j = 0; j < count && start + 2 + j * 2 + 1 < start + len; j++)
        {
            int comp = pkt[start + 2 + j * 2];
            int st = pkt[start + 2 + j * 2 + 1];
            string status = st switch
            {
                0 => "已断连", 4 => "入盒", 5 => "摘下", 7 => "佩戴", _ => "?" + st
            };
            if (comp == 1) State.WearingL = status;
            else if (comp == 2) State.WearingR = status;
        }
        Log.D("RFCOMM", $"ParseWearingData: L='{State.WearingL}' R='{State.WearingR}'");
    }

    private void ParseEq(byte[] pkt, int start, int len)
    {
        if (len >= 2)
            State.EqPreset = Caps.EqNames.GetValueOrDefault(pkt[start + 1], "?");
        StateChanged?.Invoke();
    }

    private void ParseBatchStatus(byte[] pkt, int start, int len)
    {
        for (int i = 0; i + 1 < len; i += 2)
        {
            byte feature = pkt[start + i];
            byte value = pkt[start + i + 1];
            if (feature == OppoProtocol.FeatureGameMain)
                State.GameMode = value != 0;
            else if (feature == OppoProtocol.FeatureDualDevice)
                State.DualDevice = value != 0;
            else if (feature == OppoProtocol.FeatureSpatial)
                State.SpatialSound = value != 0;
        }
        StateChanged?.Invoke();
    }

    /// <summary>解析多设备连接列表响应 (cmd 0x8112)</summary>
    private void ParseMultiConnect(byte[] pkt, int start, int len)
    {
        try
        {
            Log.D("RFCOMM", "ParseMultiConnect: len=" + len + ", full=" + BitConverter.ToString(pkt, start, Math.Min(len, 48)));
            var devices = new List<ConnectedDeviceInfo>();
            if (len < 2) return;

            int count = pkt[start + 1];
            if (count <= 0 || count > 8)
            {
                Log.D("RFCOMM", "ParseMultiConnect: invalid count=" + count);
                return;
            }

            int pos = start + 2;
            for (int i = 0; i < count && pos + 8 < start + len; i++)
            {
                // 官方 CommandUtil.h 布局：[addr(6)][len@6(1)][connState@7(1)][flag@8(1)][nameLen@9(1)][name]。
                //   connState：0=断开 2=已连接；flag 位：bit0=当前设备 bit1=音频激活 bit2=主音频，bit3-5=设备类型。
                //   [+6] 字节官方仅用于计算下一元素偏移，不作展示字段。
                var addr = string.Join(":", Enumerable.Range(0, 6).Select(j => pkt[pos + j].ToString("X2")));
                pos += 6;

                int elemByte6 = pkt[pos++]; // [+6] 官方偏移用字节，展示层不用
                int connState = pkt[pos++]; // [+7] 0=断开 2=已连接
                int flag = pkt[pos++];      // [+8] 真正的状态位
                int nameLen = pkt[pos++];   // [+9]

                if (nameLen < 0 || pos + nameLen > start + len)
                {
                    Log.D("RFCOMM", "ParseMultiConnect: device[" + i + "] invalid nameLen=" + nameLen);
                    break;
                }

                string deviceName = nameLen > 0
                    ? System.Text.Encoding.UTF8.GetString(pkt, pos, nameLen).TrimEnd("\0".ToCharArray())
                    : "Device " + addr.Substring(Math.Max(0, addr.Length - 5));
                pos += Math.Max(nameLen, 0);

                bool isCurrent = (flag & 0x01) != 0;      // 官方 bit0 = 当前设备
                bool isAudioActive = (flag & 0x02) != 0;  // 官方 bit1 = 音频激活
                bool isMainAudio = (flag & 0x04) != 0;    // 官方 bit2 = 主音频

                devices.Add(new ConnectedDeviceInfo
                {
                    Address = addr,
                    DeviceName = deviceName,
                    ConnectionState = connState,
                    DeviceType = 0,
                    IsCurrentDevice = isCurrent,
                    IsAudioActive = isAudioActive,
                    IsMainAudioDevice = isMainAudio,
                });
                Log.D("RFCOMM", "ParseMultiConnect: device[" + i + "] addr=" + addr + ", name=\"" + deviceName + "\", connState=" + connState + ", flag=0x" + flag.ToString("X2") + ", cur=" + isCurrent);
            }

            if (devices.Count > 0)
            {
                // 当前设备排最前
                devices = devices.OrderByDescending(d => d.IsCurrentDevice).ThenBy(d => d.DeviceName).ToList();
                State.ConnectedDevices = devices;
                State.MultiConnectListUpdatedAt = DateTime.Now;
                Log.D("RFCOMM", "ParseMultiConnect: 列表更新 " + devices.Count + " 个设备: " + string.Join(", ", devices.Select(d => d.DeviceName)));
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Ex("RFCOMM", "ParseMultiConnect", ex);
        }
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
                    // 智能切换档位需单独查询（官方 [0x03,0x01]），拿设备实时计算档位
                    if (HasSmartMode())
                    {
                        TrySend(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAncIntelligent);
                        Thread.Sleep(100);
                    }
                    _transport.Poll(500);
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
                        _transport.Send(OppoProtocol.CmdRegisterNotify, OppoProtocol.PayRegisterWear);
                        Thread.Sleep(100);
                        _transport.Poll(400);

                        if (TrySend(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty))
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
