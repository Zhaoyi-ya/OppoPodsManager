using System;
using System.Threading;
using System.Threading.Tasks;

namespace OppoPodsManager;

/// <summary>
/// 前后端契约。前端只依赖本接口与其暴露的数据结构。
/// 后端（PodManager + Transport/Protocol）可任意重构内部逻辑，
/// 只要本接口签名与下列数据结构不变。
///
/// 契约数据结构：
///   PodState              运行时状态（电量/ANC/佩戴/功能开关/多设备）
///   DeviceCapabilities    设备能力（型号/EQ/ANC 映射/功能标志）
///   ConnectedDeviceInfo   多设备列表项
/// </summary>
public interface IPodManager : IDisposable
{
    /// <summary>当前设备运行时状态（电量/ANC/佩戴/功能开关/多设备列表）。</summary>
    PodState State { get; }

    /// <summary>当前设备能力（型号识别、EQ、ANC 映射、功能标志）。</summary>
    DeviceCapabilities Caps { get; }

    /// <summary>最近一次错误信息，无错误为 null。</summary>
    string? LastError { get; }

    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>状态变化通知（后端线程触发，UI 需自行 marshal 到 UI 线程）。</summary>
    event Action? StateChanged;

    /// <summary>设置类命令失败/超时通知（参数为可读描述）。后端线程触发。</summary>
    event Action<string>? CommandFailed;

    // ==================== 连接生命周期 ====================

    /// <summary>建立连接（内部完成设备发现 + 初始化握手）。启动或重连时调用一次。</summary>
    Task ConnectAsync();

    /// <summary>连接后的持续轮询（周期性回读电量/ANC/开关等），直到 ct 取消或设备断开。连接成功后启动。</summary>
    Task PollAsync(CancellationToken ct);

    /// <summary>主动断开连接（幂等）。</summary>
    void Disconnect();

    // ==================== 降噪 (ANC) ====================

    /// <summary>设置降噪模式。mode 取 Caps.AncOptions 里的 Key 之一。</summary>
    void SendAnc(string mode);

    // ==================== 音效 ====================

    /// <summary>空间音效开关（feature 0x1B）。UI 读 Caps.HasSpatialSound 决定是否显示。</summary>
    void SendSpatial(bool on);

    /// <summary>空间音频三模式（cmd 0x0422）。mode="Off"/"Fixed"/"Track"。UI 读 Caps.HasSpatialAudio。</summary>
    void SendSpatialAudio(string mode);

    /// <summary>大师调音 EQ。name 取 Caps.EqPresets 的键。</summary>
    void SendEq(string name);

    /// <summary>新建带名称的自定义 EQ 预设（action=1）。仅用于创建，设备端保存后可通过 0x8122 回读新 eqId。</summary>
    void SendCustomEq(int[] gains, string name);

    /// <summary>更新/应用已有 EQ 预设（action=2）。带原 eqId + 完整 EqInfo，用于编辑滑块预览与保存已有预设。</summary>
    void UpdateCustomEq(byte eqId, int[] gains, string name, int minValue = -6, int maxValue = 6);

    /// <summary>删除设备端 EQ 预设。eqId 取自 State.DeviceEqEntries 条目的 EqId。</summary>
    void DeleteEq(int eqId);

    /// <summary>查询设备端全部 EQ 信息（含自定义预设名），结果经 StateChanged → State.DeviceEqEntries。</summary>
    void SendQueryEqAll();

    /// <summary>游戏音效开关（cmd 0x0423）。关闭发 type=0、开启发 Caps.GameSoundType。</summary>
    void SendGameSound(bool on);

    // ----- 音效增强互斥组（游戏音效 ↔ 调音 ↔ 空间音效）-----
    // 型号 gameSoundMutexes 决定哪些项互斥。若互斥，可将它们做成单选控件。

    /// <summary>读当前生效的音效增强项（从设备状态推导），用于单选控件回显。</summary>
    AudioEnhancement CurrentEnhancement();

    /// <summary>设置音效增强（互斥单选）。选一项会自动关互斥的其它项；mode=Eq 时须传 eqName。</summary>
    void SetAudioEnhancement(AudioEnhancement mode, string? eqName = null);

    // ==================== 功能开关 ====================

    /// <summary>游戏模式（低延迟）。compatible=true 额外发低延迟兼容 feature。</summary>
    void SendGameMode(bool on, bool compatible = false);

    /// <summary>双设备/多设备连接总开关（feature 0x11）。</summary>
    void SendDualDevice(bool on);

    // ==================== 主动查询 ====================

    /// <summary>主动查询电量（结果经 StateChanged 通知，读 State.Battery）。通常由 PollAsync 自动回读。</summary>
    void SendBattery();

    // ==================== 多设备管理（cmd 0x0429）====================
    // 用法：先 SendMultiConnectInfo() 刷新，从 State.ConnectedDevices 拿列表。
    // 每项含 Address / ConnectionState / IsCurrentDevice / IsAudioActive / IsMainAudioDevice。
    //   - IsCurrentDevice=true（本机）：不提供断开/解绑。
    //   - ConnectionState==2 已连接、非当前：可断开；想切音频输出到它 → 设为优先。
    //   - ConnectionState==0 已断开：可连接。
    //   - IsAudioActive=true = 此刻正在放音的设备。

    /// <summary>刷新多设备连接列表（结果经 StateChanged 通知，读 State.ConnectedDevices）。</summary>
    void SendMultiConnectInfo();

    /// <summary>连接指定手持设备。targetAddress 格式为 "AA:BB:CC:DD:EE:FF"。</summary>
    void SendMultiConnectConnect(string targetAddress);

    /// <summary>断开指定手持设备。</summary>
    void SendMultiConnectDisconnect(string targetAddress);

    /// <summary>设为优先设备：把音频活动/主通道切到该设备。</summary>
    void SendMultiConnectSetPriority(string targetAddress);

    /// <summary>取消配对/解绑指定手持设备。</summary>
    void SendMultiConnectUnpair(string targetAddress);

    /// <summary>[兼容旧接口] connect=true=连接、false=断开。新代码建议使用语义化方法。</summary>
    void SendOperateHandheld(string targetAddress, bool connect = true);

    // ==================== UI 辅助 ====================

    /// <summary>UI 注入"用户刚操作"时间戳钩子，用于抑制轮询回读在短时间内覆盖用户刚做的选择。</summary>
    void SetFeatureUserSetHook(Action hook);
}
