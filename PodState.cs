using System;
using System.Collections.Generic;

namespace OppoPodsWPF;

/// <summary>已连接设备信息（多设备连接列表中的一项）</summary>
public class ConnectedDeviceInfo
{
    public string Address { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int DeviceType { get; set; }         // 0=phone, 1=computer, 2=tablet, 3=watch
    public int ConnectionState { get; set; }    // 0=disconnected, 1=connecting, 2=connected
    public bool IsAudioActive { get; set; }
    public bool IsCurrentDevice { get; set; }
    public bool IsMainAudioDevice { get; set; }

    /// <summary>显示名称（当前设备加标记�?/summary>
        public string ConnectionStatus => ConnectionState switch
    {
        2 => IsCurrentDevice ? "当前设备" : "已连接",
        1 => "连接中",
        _ => "已断开"
    };

    public string DisplayName => (IsCurrentDevice ? "▶ " : "") + DeviceName;
}

public class PodState
{
    public Dictionary<string, (int Level, bool Charging)?> Battery { get; } = new();
    public string AncMode { get; set; } = "?";
    public string EqPreset { get; set; } = "?";
    public string WearingL { get; set; } = "";
    public string WearingR { get; set; } = "";
    public bool Connected { get; set; }
    public bool SpatialSound { get; set; }
    public string SpatialMode { get; set; } = "Off";
    public bool GameMode { get; set; }
    public bool DualDevice { get; set; }

    /// <summary>���豸�����б�����Զ����̼���</summary>
    public List<ConnectedDeviceInfo> ConnectedDevices { get; set; } = new();

    /// <summary>���豸�б�������ʱ��</summary>
    public DateTime MultiConnectListUpdatedAt { get; set; } = DateTime.MinValue;
}
