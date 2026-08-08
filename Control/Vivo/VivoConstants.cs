namespace OppoPodsManager.Control.Vivo;

// vivo TWS GAIA 协议常量（HyperEars Python 移植，GPL-3.0-only）。
// 命令身份经 2026-08-08 官方 App HCI 抓包 + APK jadx 反编译（EarbudSettingsFetcher /
// DualConnectionManager）双重验证：抓包解出线上字节，反编译把字节映射到 App 内部命令 ID（decimal
// 258~635），report 帧 = query 帧 | 0x8000，与 OPPO/GAIA 通知约定一致。
internal static class VivoConstants
{
    // vivo 私有 RFCOMM 服务 UUID（与 Kotlin VivoEarbudAdapter.VIVO_GAIA_UUID 一致）。
    public static readonly Guid VivoServiceId = new("00000837-D102-11E1-9B23-00025B00A5A5");

    public const byte Preamble = 0xFF;
    public const ushort GaiaVendor = 0x000A;
    public const ushort VivoVendor = 0x001B;

    public const ushort Handshake = 0x0300;
    public const ushort HandshakeResponse = 0x8300;

    // ---- 注册通知（OPPO 同源，抓包实证）----
    // 官方 App 连接后依次发出下列空载荷帧，耳机回 0x82xx 应答后即开始主动推送各状态 report 帧。
    // 与 OPPO 的 RegisterNotifications(0x0205) 同编号、同语义：空载荷一发即开推。
    // 抓包序列：0x0202→0x8202、0x0203→0x8203、0x0204→0x8204、0x0205→0x8205、0x0206→0x8206。
    public const ushort RegisterNotificationsStart  = 0x0202;
    public const ushort RegisterNotificationsQuery  = 0x0203;
    public const ushort RegisterNotificationsEnable = 0x0204;
    public const ushort RegisterNotification        = 0x0205;
    public const ushort RegisterNotificationsEnd    = 0x0206;
    public const ushort RegisterNotificationAck     = 0x8205;

    public const ushort QueryBattery = 0x0207;
    public const ushort ReportBattery = 0x8207;

    public const ushort SetNoiseMode = 0x0130;
    public const ushort QueryNoiseMode = 0x0230;
    public const ushort AckNoiseMode = 0x8130;
    public const ushort ReportNoiseMode = 0x8230;

    // vivo 降噪模式字节（对应 vivo_protocol.NoiseMode）：ANC=0, OFF=1, TRANSPARENCY=2。
    public const byte NoiseAnc = 0;
    public const byte NoiseOff = 1;
    public const byte NoiseTransparency = 2;

    // ---- 佩戴检测（反编译实证 259 / 525）----
    // 开关 set_wear_monitor = 0x0103（TX）/ 0x8103（ACK）；状态上报 0x820D（RX，抓包 20 次）。
    // 0x820D 负载 [status:0][flags]，flags 位域：0x01=右耳佩戴、0x02=左耳佩戴、0x0C=在充电盒。
    //   flags=0x03 双耳佩戴、0x02 仅右、0x04 仅左、0x00 摘下、0x0C 在盒充电。
    public const ushort SetWearDetection   = 0x0103;
    public const ushort AckWearDetection   = 0x8103;
    public const ushort QueryWearStatus    = 0x020D;
    public const ushort ReportWearStatus   = 0x820D;

    // ---- 音效场景（反编译实证 280，set_audio_effect）----
    // 设置 0x0118（TX）/ 上报 0x8118（RX）。上报负载 [status:0][scene]，scene∈{0,1,2,3,5} 为场景枚举。
    //   0=关闭 1=均衡 2=重低音 3=清澈人声 5=其他（实测出现值）。
    // 注意：此即旧 0x0118，此前被误标为“空间音效”，反编译已证实它是音效场景而非空间音频。
    public const ushort SetSoundEffectScene    = 0x0118;
    public const ushort ReportSoundEffectScene = 0x8118;

    // ---- 空间音频（反编译实证 536，set_spatial_audio；TWS 3e 无此功能）----
    // 设置/查询共用命令字 0x0218，耳机上报 0x8218（此前误把 0x0118 当作空间，已更正）。
    public const ushort SetSpatialSound    = 0x0218;
    public const ushort QuerySpatialSound  = 0x0218;
    public const ushort ReportSpatialSound = 0x8218;

    // ---- 游戏低延迟（命令字未实证，沿用 HyperEars 占位 0x0220 / 0x8220）----
    // 注意：旧 0x0120 / 0x8120 经反编译实证为 audio_play_state（媒体播放状态），并非游戏模式，已移出。
    public const ushort SetGameMode    = 0x0220;
    public const ushort QueryGameMode  = 0x0220;
    public const ushort AckGameMode    = 0x8220;
    public const ushort ReportGameMode = 0x8220;

    // ---- 媒体播放状态（反编译实证 288，audio_play_state；非游戏模式）----
    // 查询 0x0120 / 上报 0x8120。负载 [status:0][state]，state≠0 表示正在播放。
    public const ushort QueryAudioPlayState    = 0x0120;
    public const ushort ReportAudioPlayState   = 0x8120;

    // ---- 查找耳机 ----
    // 抓包实证：官方 App 走 QUERY 0x0231（payload=单字节子命令 05/06/07），REPORT 0x8231
    // （载荷 = 00 <子命令> <v1> <v2>，如 00 05 0b 0b）。SET 0x0131 此前实测仅 ACK 不响铃，
    // 真实触发 payload 仍需“单独点查找并抓到 0x0131 SET 帧”确认，当前仍按单字节 0/1 发送。
    public const ushort SetFindDevice    = 0x0131;
    public const ushort AckFindDevice    = 0x8131;
    public const ushort QueryFindDevice  = 0x0231;
    public const ushort ReportFindDevice = 0x8231;

    // ---- 多连接（双设备）命令（2026-08-08 开源 Vivopods 项目交叉验证，vivo TWS5 / iQOO 高阶机型）----
    // 源：Vivopods-Windows VivoCommands.cs（HyperEars 抓包 + 官方 APK jadx 反编译双重确认）。
    // 双连设备列表上报 0x8249 结构：[status:1=0][count:1] 后每台 [MAC:6][未知:3][state:1][nameLen:1][UTF8名]。
    // 设备状态字节：0=断，1=保持(已记忆/配对但非当前)，2=连为当前(活跃音频源)。
    // 设置设备列表 0x014A 为全量下发，每条仅 [MAC:6][state:1]（7 字节，不含未知/名称）。
    // 注意：TWS 3e 官方矩阵不含 dual_connection(17)，本实现按能力白名单自动隐藏；高阶机型待志愿者真机验证命令效果。
    public const ushort QueryMultiConnect      = 0x0249;
    public const ushort ReportMultiConnect      = 0x8249;
    public const ushort SetMultiConnect         = 0x014A;  // 全量设置设备列表（每条 MAC[6]+state[1]）
    public const ushort AckMultiConnect         = 0x814A;
    public const ushort EnableMultiConnect      = 0x014C;  // [enable:1] 开关双连功能
    public const ushort AckEnableMultiConnect   = 0x814C;
    public const ushort RemoveMultiConnect      = 0x014D;  // [目标MAC:6] 移除已记忆设备
    public const ushort AckRemoveMultiConnect   = 0x814D;

    // ---- 手机时间同步（耳机主动请求）----
    // 耳机经 0x8509 向主机索要时间，主机须应答 0x0509（7 字节：年/100, 年%100, 月, 日, 时, 分, 秒）。
    public const ushort PeerTimeRequest      = 0x8509;
    public const ushort HostTimeResponse     = 0x0509;

    // ---- 埋点上报（吸收忽略）----
    public const ushort TelemetryReport      = 0x8224;
}

// 型号画像：不同 vivo/iQOO 型号 GAIA 版本与降噪载荷不同（对应 vivo_protocol.Profile）。
// 源画像：Air3 Pro(v3, 空查询, [4,0])、TWS 3e(v3, 空查询, [3])、家族默认(v4, 查询[0], [3,1])。
// 握手与电量固定走 GAIA v4；仅降噪命令使用画像内的 GaiaVersion。
public sealed record VivoProfile(int GaiaVersion, byte[] NoiseQueryPayload, byte[] NoiseSetSuffix)
{
    public static readonly VivoProfile Air3ProV3 = new(3, [], [4, 0]);
    public static readonly VivoProfile Tws3eV3 = new(3, [], [3]);
    public static readonly VivoProfile FamilyDefaultV4 = new(4, [0], [3, 1]);
}
