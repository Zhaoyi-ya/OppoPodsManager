using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>OPPO 私有 RFCOMM 协议定义。命令字和 feature ID 各型号通用。</summary>
public static partial class OppoProtocol
{
    /// <summary>OPPO SPP 服务 UUID，所有支持设备共用</summary>
    public static readonly Guid OppoSppUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");

    /// <summary>协议帧头</summary>
    public const byte Header = 0xAA;
    /// <summary>序列号</summary>
    public const byte Seq = 0xF0;

    // ========== 命令字 ==========
    public const ushort CmdBattery = 0x0106;         // 查询电量
    public const ushort CmdBatteryResp = 0x8106;     // 电量响应
    public const ushort CmdAnc = 0x0404;             // 设置降噪模式
    public const ushort CmdQueryAnc = 0x010C;        // 查询降噪状态
    public const ushort CmdAncResp = 0x810C;         // 降噪状态响应
    public const ushort CmdActiveReport = 0x0204;    // 主动通知事件（payload[0]=子类型，NotificationCommandManager 分发）
    public const ushort CmdSetFeature = 0x0403;      // 设置功能开关
    public const ushort CmdSetEq = 0x0406;           // 设置 EQ 预设
    public const ushort CmdQueryEq = 0x010F;         // 查询当前 EQ
    public const ushort CmdEqResp = 0x810F;          // EQ 查询响应
    public const ushort CmdEqNotify = 0x0504;        // EQ 变更通知
    public const ushort CmdSpatialAudio = 0x0422;    // 空间音频三模式（Off/Fixed/Track）
    public const ushort CmdRegisterNotify = 0x0205;  // 订阅设备主动通知
    public const ushort CmdBatchQuery = 0x010D;      // 批量查询功能状态
    public const ushort CmdBatchQueryResp = 0x810D;  // 批量查询响应
    public const ushort CmdMultiConnectInfo = 0x0112;  // 查询多设备连接列表
    public const ushort CmdMultiConnectResp = 0x8112;  // 多设备列表响应
    public const ushort CmdOperateHandheld = 0x0429;   // 切换多设备中的活动设备
    public const ushort CmdQueryProductId = 0x0103;    // 查询远程 Product ID（设备识别主键）
    public const ushort CmdProductIdResp = 0x8103;     // Product ID 响应

    // ========== 完整命令目录（0x100-0x133 查询 / 0x400-0x43B 设置）==========
    // 来源：melody 16.8.1 逆向，命令号取自 PollCommandManager/SetCommandManager 的
    //   "<fnName> UNSUPPORTED cmd=0x<num>" 日志（LA7/b、LB7/h、LC7/a、Lm、Ln、Lu 等 R8 合成类）。
    // 命令层用 melody 命令号，经 SPP 0xAA 帧承载；查询响应 = 命令 | 0x8000。
    // 命令名与 melody getXxx/setXxx 方法对应。

    // ----- 设备信息查询（0x1xx，getXxx；响应 0x81xx）-----
    public const ushort CmdQueryCapability   = 0x0100;  // getRemoteCapability 基础能力
    public const ushort CmdQueryMtu          = 0x0101;  // getRemoteMTU 远程 MTU
    public const ushort CmdQueryVendorId     = 0x0102;  // getRemoteVID 远程 Vendor ID
    // 0x0103 = getRemotePID（= CmdQueryProductId，上方已定义）
    public const ushort CmdQueryVersion      = 0x0105;  // getRemoteVersion 远程固件版本
    public const ushort CmdQueryUpgradeCap   = 0x0107;  // getUpgradeCapability 升级能力
    public const ushort CmdQueryFunctionKey  = 0x0108;  // getKeyFunction 按键功能（设置为 setKeyFunction 0x0402）
    public const ushort CmdQueryEarStatus    = 0x0109;  // getEarBudsStatus 耳机佩戴状态
    public const ushort CmdQueryColorId      = 0x010B;  // getEarBudsColorID 耳机颜色 ID
    // 0x010C = getCurrentNoiseReductionMode / getNoiseReductionSwitchMode / getIntelligentNoiseReductionMode
    //          （= CmdQueryAnc，上方已定义）
    public const ushort CmdQueryFeatureState = 0x010D;  // getFeatureSwitchStatus 功能开关状态（= CmdBatchQuery）
    // 0x010F = getCurrentEqualizerMode（= CmdQueryEq，上方已定义）
    // 0x0112 = getMultiConnectInformation（= CmdMultiConnectInfo，上方已定义）
    public const ushort CmdQueryCodecType    = 0x0114;  // getCurrentCodecType 当前编解码器类型
    public const ushort CmdQueryHearing      = 0x0115;  // getHearingEnhancementData 听力增强数据
    public const ushort CmdQueryHearingFilter = 0x0116; // getHearingEnhancementFilterData 听力增强滤波数据
    public const ushort CmdQueryEarRestore   = 0x0118;  // getEarRestoreData 入耳恢复数据
    public const ushort CmdQueryZenMode      = 0x0119;  // getEarBudsZenModeInformation 禅模式信息
    public const ushort CmdQueryCapBitmap    = 0x011C;  // getTriangleInfo/getRemoteCapability 能力位图（三角/GetCapability）
    public const ushort CmdQueryFreeDialog   = 0x011D;  // getFreeDialogRecoveryTime 自由对话恢复时间
    public const ushort CmdQueryEarScan      = 0x011E;  // getEarScanData 耳道扫描数据
    public const ushort CmdQueryEarScanFilter = 0x011F; // getEarScanFilterData 耳道扫描滤波数据
    public const ushort CmdQueryEarTone      = 0x0121;  // getEarToneData 耳音调数据
    public const ushort CmdQueryEqAll        = 0x0122;  // getAllEqInfo 全部 EQ 数据
    public const ushort CmdQueryCodecList    = 0x0123;  // getHighAudioCodecList 高清编解码器列表
    public const ushort CmdQueryBassEngine   = 0x0124;  // getBassEngineVale 低音引擎值
    public const ushort CmdQueryAccountKey   = 0x0125;  // getAccountKey AccountKey
    public const ushort CmdQuerySpineHistory = 0x0126;  // getSpineHistoryData 脊柱历史数据
    public const ushort CmdQueryScreenOffDelay = 0x0127; // getScreenOffBroadcastDelayTime 息屏广播延时
    public const ushort CmdQuerySpineCalib   = 0x0129;  // getSpineCalibration 脊柱校准状态
    // 0x012A：getHeadsetSpatialType —— 查询空间音频三模式当前值（Off/Fixed/Track）。
    // 响应 0x812A = [status(1)][spatialType(1)]，spatialType 0=Off 1=Fixed 2=Track。
    public const ushort CmdQueryHeadsetSpatial = 0x012A;  // getHeadsetSpatialType 空间音频三模式当前值
    public const ushort CmdHeadsetSpatialResp  = 0x812A;  // 空间音频三模式查询响应
    public const ushort CmdQueryGameSound    = 0x012B;  // getGameSoundInfo 游戏音效信息 [status][selectType][count][types..]
    public const ushort CmdQueryAiSummary    = 0x012E;  // getAISummaryType AI 摘要类型
    public const ushort CmdQueryVolumeValue  = 0x0130;  // getVolumeValueInfo 音量档位信息
    public const ushort CmdQueryAiTranslate  = 0x0131;  // getAITranslationAppStatus AI 翻译 App 状态
    // 0x012B = getGameSoundInfo（= CmdQueryGameSound）
    public const ushort CmdQueryMultiPriority = 0x0132; // getMultiConnectPriorityDevice 多连接优先设备
    public const ushort CmdMultiPriorityResp  = 0x8132; // 多连接优先设备响应
    public const ushort CmdQueryTapLevel     = 0x0133;  // getTapLevelSettingValue 敲击力度档位

    // ----- 高级设置（0x4xx，setXxx；响应 0x84xx，SetCommandManager 统一处理）-----
    public const ushort CmdSetFindMode       = 0x0400;  // setFindMode 查找耳机模式（旧注释误标为"全局开关"）
    public const ushort CmdSetKeyFunction    = 0x0402;  // setKeyFunction 设置按键功能
    public const ushort CmdSetSwitchFeature  = 0x0403;  // setSwitchFeature 通用功能开关（= CmdSetFeature）[featureType][status]
    public const ushort CmdSetNoiseMode      = 0x0404;  // setCurrentNoiseReduction / setSupportNoiseReduction（= CmdAnc）
    public const ushort CmdSetCompactness    = 0x0405;  // switchCompactnessDetectionStatus 贴合度检测开关
    public const ushort CmdSetEqPreset       = 0x0406;  // setEqMode 设置 EQ 预设（= CmdSetEq）
    public const ushort CmdSetRelatedDevice  = 0x0408;  // setRelatedDeviceInfo 关联设备信息
    public const ushort CmdSetHearingDetect  = 0x040D;  // processHearingEnhancementDetection 听力增强检测
    public const ushort CmdSetCodec          = 0x040E;  // 设置编解码器（setCodecType）
    public const ushort CmdSetCameraStatus   = 0x040F;  // setSystemCameraStatus 系统相机状态
    public const ushort CmdSetZenMode        = 0x0410;  // setZenModeCheckInformation 禅模式
    public const ushort CmdSetEarRestore     = 0x0411;  // setEarRestoreData 入耳恢复数据
    public const ushort CmdSetPersonalNoise  = 0x0412;  // setPersonalizedNoiseReduction 个性化降噪
    public const ushort CmdSetHostTriangle   = 0x0413;  // setHostTriangleInfo 主机三角信息
    public const ushort CmdSetFreeDialogTime = 0x0414;  // setFreeDialogRecoveryTime 自由对话恢复时间
    public const ushort CmdSetEarScanData    = 0x0415;  // sendProcessEarScanData 耳道扫描数据
    public const ushort CmdSetTone           = 0x0417;  // setTone 设置耳音调（旧注释误标为"空间音频旧路"）
    public const ushort CmdSetEqDetail       = 0x0418;  // setEqInfo 设置详细 EQ（自定义频段）
    public const ushort CmdSetHighAudioCodec = 0x041A;  // setHighAudioCodecType 高清编解码器类型
    public const ushort CmdSetBassEngine     = 0x041B;  // setBassEngineValue 低音引擎值
    public const ushort CmdSetPhoneSpatial   = 0x041E;  // setSpatialAudioType 手机侧空间音频（非耳机 0x422）
    public const ushort CmdSetBuildModel     = 0x041F;  // setDeviceBuildModel 设置机型 Build.MODEL
    public const ushort CmdSetGameStatus     = 0x0420;  // setGameStatus 游戏模式状态（独立命令，非 feature 开关）
    public const ushort CmdSetSpineRange     = 0x0421;  // setSpineRangeDetection 脊柱范围检测
    // 0x0422 = setHeadsetSpatialType（= CmdSpatialAudio，上方已定义，空间音频三模式）
    public const ushort CmdSetFeatureSwitch  = 0x0423;  // setGameSoundTypeEnable 游戏音效 [type][enable]
    public const ushort CmdSetLeAudioAction  = 0x0424;  // setLeAudioAction LE Audio 动作
    public const ushort CmdSetAiSummary      = 0x0425;  // setAISummaryType AI 摘要类型
    public const ushort CmdSetAiPromptSound  = 0x0426;  // syncAIPromptSound AI 提示音同步
    public const ushort CmdSetVolumeValue    = 0x0427;  // setVolumeValueInfo 音量档位
    public const ushort CmdSetTranslateApp   = 0x0428;  // setTranslationAppStatus 翻译 App 状态
    // 0x0429 = operateMultiConnectHandheldDevice（= CmdOperateHandheld，上方已定义）
    public const ushort CmdSetTapLevel       = 0x042D;  // setTapLevelSettingValue 敲击力度档位
    public const ushort CmdSetVoiceMultiConv = 0x042E;  // setVoiceMultiConversationStatus 多方通话状态
    public const ushort CmdFindDevice        = 0x0435;  // 查找设备（旧系；查找模式实际走 setFindMode 0x0400）

    // ----- 响应 -----
    public const ushort CmdSpatialAudioResp  = 0x8422;  // 空间音频 SET 响应（0x0422 ACK）

    // ========== 通知注册响应族（NotificationCommandManager 分发，耳机→手机）==========
    public const ushort CmdNotifyCapabilityResp = 0x8200;  // 通知能力响应（保存耳机支持的事件集）
    public const ushort CmdRegisterNotifyResp   = 0x8201;  // 单条注册通知响应（含 status+event）
    public const ushort CmdRegisterNotifyEvent  = 0x8202;  // 注册后携带的事件（内部再走子类型分发）
    public const ushort CmdCancelNotifyResp     = 0x8203;  // 取消注册通知响应
    public const ushort CmdRegisterMultiResp    = 0x8205;  // 批量注册通知响应 = 初始化握手完成 ACK

    // ========== 0x0204 通知事件子类型（payload[0] 分发键）==========
    public const byte EvtBattery       = 0x01;  // 电池信息 List<BatteryInfo>
    public const byte EvtEarBudsStatus = 0x02;  // 佩戴/入耳状态 List<StatusInfo>
    public const byte EvtNoiseMode     = 0x03;  // 降噪模式变更（次字节区分旧/新/智能）
    public const byte EvtCompactness   = 0x04;  // 贴合度检测
    public const byte EvtGameMode      = 0x05;  // 游戏模式开关
    public const byte EvtMultiConnect  = 0x06;  // 多设备连接状态
    public const byte EvtHearingDetect = 0x08;  // 听力检测状态
    public const byte EvtCodecType     = 0x09;  // 编解码器类型
    public const byte EvtZenMode       = 0x0A;  // 禅模式开关
    public const byte EvtPersonalNoise = 0x0B;  // 个性化降噪结果
    public const byte EvtTriangle      = 0x0D;  // 空间音频三角信息
    public const byte EvtEarScan       = 0x0E;  // 耳道扫描结果
    public const byte EvtGaming        = 0x0F;  // 多连接游戏/手持/点击等级公共事件
    public const byte EvtOneshot       = 0x10;  // Oneshot 状态
    public const byte EvtToneChange    = 0x11;  // 耳音调变更

    /// <summary>0x0204 子类型 → 可读名称（用于日志）。</summary>
    public static string ActiveReportName(int subType) => subType switch
    {
        EvtBattery       => "电池",
        EvtEarBudsStatus => "佩戴状态",
        EvtNoiseMode     => "降噪变更",
        EvtCompactness   => "贴合检测",
        EvtGameMode      => "游戏模式",
        EvtMultiConnect  => "多连接状态",
        EvtHearingDetect => "听力检测",
        EvtCodecType     => "编解码",
        EvtZenMode       => "禅模式",
        EvtPersonalNoise => "个性化降噪",
        EvtTriangle      => "空间音频三角",
        EvtEarScan       => "耳道扫描",
        EvtGaming        => "游戏/手持公共事件",
        EvtOneshot       => "Oneshot",
        EvtToneChange    => "耳音调",
        _                => "未知(0x" + subType.ToString("X2") + ")"
    };

    // ========== 空间音频模式（cmd 0x0422）==========
    public static readonly byte[] SpatialOff   = { 0x00 };
    public static readonly byte[] SpatialFixed = { 0x01 };
    public static readonly byte[] SpatialTrack = { 0x02 };

    /// <summary>构造 OPPO 协议帧</summary>
    public static byte[] BuildPacket(ushort cmd, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        int totalLen = 7 + payload.Length;
        var pkt = new byte[2 + totalLen];
        pkt[0] = Header;
        pkt[1] = (byte)totalLen;
        pkt[2] = 0x00;
        pkt[3] = 0x00;
        pkt[4] = (byte)(cmd & 0xFF);
        pkt[5] = (byte)((cmd / 256) & 0xFF);
        pkt[6] = Seq;
        pkt[7] = (byte)(payload.Length & 0xFF);
        pkt[8] = (byte)((payload.Length / 256) & 0xFF);
        Buffer.BlockCopy(payload, 0, pkt, 9, payload.Length);
        return pkt;
    }

    /// <summary>构造功能开关帧（cmd 0x0403 + feature + on/off）</summary>
    public static byte[] BuildFeaturePacket(byte feature, bool on) =>
        BuildPacket(CmdSetFeature, new[] { feature, (byte)(on ? 0x01 : 0x00) });

    // ========== 预构建的常用包 ==========
    public static readonly byte[] PktBattery = BuildPacket(CmdBattery);
    public static readonly byte[] PktQueryAnc = BuildPacket(CmdQueryAnc, new byte[] { 0x01, 0x01 });
    public static readonly byte[] PktQueryEq = BuildPacket(CmdQueryEq);

    /// <summary>订阅主动通知：电池 + 佩戴 + ANC</summary>
    public static readonly byte[] PktRegisterNotify = BuildPacket(CmdRegisterNotify,
        new byte[] { 0x01, 0x01, 0x02, 0x02 });

    /// <summary>单独订阅佩戴通知（部分设备需要单独注册，使用 action=0x02）</summary>
    public static readonly byte[] PktRegisterWear = BuildPacket(CmdRegisterNotify,
        new byte[] { 0x02, 0x02 });

    /// <summary>批量查询包，查询 12 个功能 feature 的状态</summary>
    public static readonly byte[] PktBatchQuery = BuildPacket(CmdBatchQuery,
        new byte[] { 0x0B, 0x05, 0x04, 0x0B, 0x11, 0x13, 0x18, 0x06, 0x1B, 0x1C, 0x27, 0x28 });

    /// <summary>查询多设备连接列表</summary>
    public static readonly byte[] PktMultiConnectInfo = BuildPacket(CmdMultiConnectInfo);

    /// <summary>设备名称匹配的品牌关键词</summary>
    public static readonly string[] SupportedBrands = { "OPPO", "OnePlus", "realme" };

    /// <summary>根据空间音频模式名生成设置帧。</summary>
    public static byte[] PktSpatialAudio(string mode) => mode switch
    {
        "Fixed"  => BuildPacket(CmdSpatialAudio, SpatialFixed),
        "Track"  => BuildPacket(CmdSpatialAudio, SpatialTrack),
        _        => BuildPacket(CmdSpatialAudio, SpatialOff),
    };

    // ===== 载荷构造（供传输层 Send(cmd, payload)；帧封装交给 IFrameCodec）=====
    public static readonly byte[] PayEmpty = { };
    /// <summary>查询设备端 EQ 预设列表：1 个 feature，类型 ID=5（equalizer）。</summary>
    public static readonly byte[] PayQueryEqAll = { 0x01, 0x05 };
    public static readonly byte[] PayQueryAnc = { 0x01, 0x01 };
    /// <summary>查询智能切换实时档位（0x10C + [0x04,0x01]，响应 subType=4=IntelligentNoiseModeInfo）。</summary>
    public static readonly byte[] PayQueryAncIntelligent = { 0x04, 0x01 };
    public static readonly byte[] PayRegisterNotify = { 0x01, 0x01, 0x02, 0x02 };
    public static readonly byte[] PayRegisterWear = { 0x02, 0x02 };
    public static readonly byte[] PayBatchQuery = { 0x0B, 0x05, 0x04, 0x0B, 0x11, 0x13, 0x18, 0x06, 0x1B, 0x1C, 0x27, 0x28 };

    public static byte[] FeaturePayload(byte feature, bool on)
    {
        return new byte[] { feature, (byte)(on ? 0x01 : 0x00) };
    }

    /// <summary>空间音频 mode 名 → 载荷字节。</summary>
    public static byte[] SpatialPayload(string mode) => mode switch
    {
        "Fixed" => SpatialFixed,
        "Track" => SpatialTrack,
        _ => SpatialOff,
    };

    /// <summary>设置编解码器（cmd 0x040E）：[codec(1)]。</summary>
    public static byte[] CodecPayload(byte codec) => new[] { codec };

    /// <summary>功能开关设置（cmd 0x0423）：[value(1)][enable(1)]。</summary>
    public static byte[] FeatureSwitchPayload(byte value, bool enable) =>
        new[] { value, (byte)(enable ? 0x01 : 0x00) };

    /// <summary>查找设备（cmd 0x0435）：[action(1)] 0=停止 1=开始。</summary>
    public static byte[] FindDevicePayload(bool start) => new[] { (byte)(start ? 0x01 : 0x00) };

    /// <summary>设置机型（cmd 0x041F）：[len(1)][Build.Model UTF-8]。</summary>
    public static byte[] BuildModelPayload(string model)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(model ?? "");
        var payload = new byte[1 + b.Length];
        payload[0] = (byte)(b.Length & 0xFF);
        Buffer.BlockCopy(b, 0, payload, 1, b.Length);
        return payload;
    }

    /// <summary>
    /// 解析远程固件版本响应（0x8105）：[status(1)][n(1)][UTF-8 CSV]。
    /// CommandUtil.k 按逗号分割、每 3 字段为一条 VersionInfo；这里返回原始版本串。
    /// </summary>
    public static string? ParseVersion(byte[] payload)
    {
        if (payload == null || payload.Length < 3 || payload[0] != 0) return null;
        try
        {
            var s = System.Text.Encoding.UTF8.GetString(payload, 2, payload.Length - 2)
                        .TrimEnd('\0').Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>解析编解码器类型响应（0x8114）：[status(1)][n(1)][id,val]×n，返回首个 codec id。</summary>
    public static int ParseCodecType(byte[] payload)
    {
        int raw;

        // SPP 短格式: [status(1), codecType(1)]
        if (payload != null && payload.Length == 2 && payload[0] == 0)
        {
            raw = payload[1];
        }
        // GATT 长格式: [status(1), count(1), id(1), val(1), ...]
        else if (payload != null && payload.Length >= 4 && payload[0] == 0)
        {
            int n = payload[1];
            if (n <= 0 || payload.Length < 2 + n * 2) return -1;
            raw = -1;
            for (int i = 0; i < n; i++)
            {
                if (payload[3 + i * 2] != 0) { raw = payload[2 + i * 2]; break; }
            }
            if (raw < 0) return -1;
        }
        else return -1;

        // 归一化: OPPO 私有编号 → A2DP 标准（SPP 和 GATT 都使用 OPPO 编号，1↔2 互换）
        raw = raw switch { 1 => 2, 2 => 1, _ => raw };
        return raw;
    }

    /// <summary>
    /// 编解码器 ID（已归一化为 A2DP 标准）→ 可读名称。
    /// </summary>
    public static string CodecName(int id) => id switch
    {
        0 => "SBC",
        1 => "AAC",
        2 => "LDAC",
        3 => "LHDC",
        4 => "LC3",
        5 => "aptX",
        6 => "aptX HD",
        7 => "aptX Adaptive",
        8 => "LHDC",
        -1 => "-",
        _ => $"未知 ({id})"
    };

    /// <summary>
    /// 解析空间音频三模式查询响应（0x812A，getHeadsetSpatialType）。
    /// 格式：[status(1)][spatialType(1)]，status!=0 或长度不足视为无效（返回 -1）。
    /// 状态非 0 报错、长度<=1 视为无效、否则取 data[1] 作为 spatialType。
    /// spatialType：0=Off 1=Fixed 2=Track（与 0x0422 SET 载荷编码一致）。
    /// </summary>
    public static int ParseHeadsetSpatialType(byte[] payload)
    {
        if (payload == null || payload.Length <= 1) return -1;
        if (payload[0] != 0) return -1;   // status 非 0 = 失败
        return payload[1];
    }

    /// <summary>空间音频 spatialType 值 → 模式名（0=Off 1=Fixed 2=Track）。</summary>
    public static string SpatialTypeToName(int type) => type switch
    {
        1 => "Fixed",
        2 => "Track",
        _ => "Off",
    };

    /// <summary>
    /// 解析 0x8103 响应载荷，返回 6 位大写十六进制 productId（匹配 JSON id），失败返回 null。
    /// 格式：[status(1)][productId(3 字节, 小端)]，status!=0 或长度!=4 视为无效。
    /// </summary>
    public static string? ParseProductId(byte[] payload)
    {
        if (payload == null || payload.Length != 4) return null;
        if (payload[0] != 0) return null;  // status 非 0 = 失败
        int id = payload[1] + payload[2] * 256 + payload[3] * 65536;
        return id.ToString("X6");
    }

    /// <summary>
    /// 解析 0x0122 getAllEqInfo 响应载荷，返回设备端所有 EQ 预设条目（含内置 + 自定义）。
    /// 格式：[count(1)][entry0][entry1]...
    /// 每个 entry：[isSelect(1)][minSigned(1)][maxSigned(1)][eqId(1)][nameLen(1)][nameUTF8...][freqCount(1)][freqLE(2)][db(1)]...频率总 3×freqCount 字节。
    /// isSelect、eqId、nameLen、freqCount 均视为 unsigned，频率为 int16 LE unsigned，dB 为 signed byte。
    /// </summary>
    public static List<EqInfoEntry> ParseEqAll(byte[] payload)
    {
        var entries = new List<EqInfoEntry>();
        if (payload == null || payload.Length < 2) return entries;  // [status][count] 至少 2 字节

        if (payload[0] != 0) return entries;  // 状态字节非零表示失败
        int count = payload[1];
        int pos = 2;

        for (int i = 0; i < count && pos < payload.Length; i++)
        {
            if (pos + 5 > payload.Length) break;

            bool isSelected  = payload[pos] != 0;
            sbyte minValue  = (sbyte)payload[pos + 1];
            sbyte maxValue  = (sbyte)payload[pos + 2];
            byte  eqId      = payload[pos + 3];
            int   nameLen   = payload[pos + 4];
            pos += 5;

            string name = "";
            if (nameLen > 0 && pos + nameLen <= payload.Length)
            {
                name = System.Text.Encoding.UTF8.GetString(payload, pos, nameLen).Trim();
                pos += nameLen;
            }

            if (pos + 1 > payload.Length) break;
            int freqCount = payload[pos++];

            var freqs = new int[freqCount];
            var gains = new int[freqCount];

            for (int j = 0; j < freqCount && pos + 3 <= payload.Length; j++)
            {
                freqs[j] = payload[pos] | (payload[pos + 1] << 8);   // int16 LE
                gains[j] = (sbyte)payload[pos + 2];                  // signed dB
                pos += 3;
            }

            entries.Add(new EqInfoEntry
            {
                EqId = eqId,
                MinValue = minValue,
                MaxValue = maxValue,
                Name = name,
                IsSelected = isSelected,
                Frequencies = freqs,
                Gains = gains,
            });
        }

        return entries;
    }

    /// <summary>
    /// 删除设备端 EQ 预设的兜底载荷（cmd 0x0418, actionType=3）。
    /// 官方 Melody 会发送完整 EqInfo；仅在找不到原条目时使用此兜底。
    /// </summary>
    public static byte[] EqDeletePayload(int eqId)
    {
        return [3, 0xFA, 0x06, (byte)eqId, 0];
    }

    /// <summary>按 Melody 的 EqInfo 形态删除：action=3 + eqId + name + frequency/db arrays。</summary>
    public static byte[] EqDeletePayload(EqInfoEntry entry)
    {
        return EqInfoPayload(3, entry.Gains, entry.Frequencies, entry.Name, entry.EqId, entry.MinValue, entry.MaxValue);
    }

    /// <summary>
    /// 自定义 EQ 频段增益载荷（cmd 0x0418），按 OPPO 官方程载荷格式编码。
    /// gains 长度应与 frequencies 一致，每元素为 int（-6 ~ +6 dB）。
    /// 频率为 int16 小端写入，dB 为 signed byte。
    /// </summary>
    public static byte[] EqDetailPayload(int[] gains, int[] frequencies)
    {
        return EqDetailPayload(gains, frequencies, "");
    }

    /// <summary>
    /// 新建自定义 EQ 预设载荷（cmd 0x0418, actionType=1）。仅用于创建新预设，eqId 必为 0。
    /// 对应 Melody 的 B8.c.i(addr, eqInfo, 1)。
    /// </summary>
    public static byte[] EqDetailPayload(int[] gains, int[] frequencies, string name, byte eqId = 0)
    {
        return EqInfoPayload(1, gains, frequencies, name, eqId, -6, 6);
    }

    /// <summary>
    /// 更新/应用已有 EQ 预设载荷（cmd 0x0418, actionType=2）。带原 eqId + 完整 EqInfo。
    /// 对应 Melody 的 B8.c.i(addr, eqInfo, 2)：编辑滑块预览与切换选中都走此 action。
    /// </summary>
    public static byte[] EqUpdatePayload(EqInfoEntry entry)
    {
        return EqInfoPayload(2, entry.Gains, entry.Frequencies, entry.Name, entry.EqId, entry.MinValue, entry.MaxValue);
    }

    private static byte[] EqInfoPayload(int actionType, int[] gains, int[] frequencies, string name, byte eqId, int minValue, int maxValue)
    {
        int count = Math.Min(gains.Length, frequencies.Length);
        count = Math.Clamp(count, 1, 32);
        byte[] nameBytes = string.IsNullOrEmpty(name) ? [] : System.Text.Encoding.UTF8.GetBytes(name);
        int nameLen = nameBytes.Length;
        // 5 字节头 + nameLen + 1 字节 freqCount + 3 字节每频段
        var payload = new byte[5 + nameLen + 1 + 3 * count];
        payload[0] = (byte)actionType;
        payload[1] = unchecked((byte)(sbyte)Math.Clamp(minValue, -128, 127));
        payload[2] = unchecked((byte)(sbyte)Math.Clamp(maxValue, -128, 127));
        payload[3] = eqId;         // eqId: 0=新建, 非0=更新
        payload[4] = (byte)nameLen;
        if (nameLen > 0)
            nameBytes.CopyTo(payload, 5);
        payload[5 + nameLen] = (byte)count;  // freqCount
        for (int i = 0; i < count; i++)
        {
            int freq = frequencies[i];
            int offset = 6 + nameLen + 3 * i;
            payload[offset]       = (byte)(freq & 0xFF);         // LE low
            payload[offset + 1]   = (byte)((freq >> 8) & 0xFF); // LE high
            payload[offset + 2]   = unchecked((byte)(sbyte)Math.Clamp(gains[i], minValue, maxValue)); // signed dB
        }
        return payload;
    }
}
