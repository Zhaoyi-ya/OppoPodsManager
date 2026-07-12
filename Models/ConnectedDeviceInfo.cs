using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OppoPodsManager;

/// <summary>
/// 多设备连接列表中的一项（来自 0x8112 响应）。
/// 前端渲染设备列表 + 决定可执行的操作时读这些字段。
/// </summary>
public class ConnectedDeviceInfo : INotifyPropertyChanged
{
    /// <summary>对端手持设备 MAC，如 "AA:BB:CC:DD:EE:FF"。</summary>
    public string Address { get; set; } = "";

    /// <summary>设备名（耳机上报的对端蓝牙名）。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>设备类型（flag bit3-5）：0=手机 1=电脑 2=平板 3=手表。</summary>
    public int DeviceType { get; set; }

    /// <summary>连接状态：0=已断开 1=连接中 2=已连接。</summary>
    public int ConnectionState { get; set; }

    /// <summary>是否音频活动设备（flag bit2）：当前正在输出音频的那台。</summary>
    public bool IsAudioActive { get; set; }

    /// <summary>是否主音频设备（flag bit1）：耳机侧标记的主音频通道设备。</summary>
    public bool IsMainAudioDevice { get; set; }

    private bool _isCurrentDevice;
    /// <summary>是否当前设备（flag bit0）：运行本程序、正在交互的主机。</summary>
    public bool IsCurrentDevice
    {
        get => _isCurrentDevice;
        set { if (_isCurrentDevice != value) { _isCurrentDevice = value; OnChanged(); OnChanged(nameof(DisplayName)); } }
    }

    /// <summary>连接状态可读文案。</summary>
    public string ConnectionStatus => ConnectionState switch
    {
        2 => IsCurrentDevice ? "当前设备" : "已连接",
        1 => "连接中",
        _ => "已断开"
    };

    /// <summary>UI 显示名称。</summary>
    public string DisplayName => DeviceName;

    /// <summary>
    /// 来自 0x8112 响应的原始设备条目字节(MAC LE 6B + elemByte6 + connState + flag + nameLen + name)。
    /// 用于重建 setRelatedDeviceInfo(0x0408) 的 payload——取消配对时删掉目标条目后原样下发。
    /// </summary>
    internal byte[] RawEntryBytes { get; set; } = Array.Empty<byte>();

    /// <summary>0x8112 响应中的 elemByte6（设备级别/类型字段，0x0408 下发时用作 type）。</summary>
    internal byte ElemByte6 { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
