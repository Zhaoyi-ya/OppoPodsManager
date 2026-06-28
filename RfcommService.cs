using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OppoPodsWPF;

public class RfcommService : IDisposable
{
    private IntPtr _socket = IntPtr.Zero;
    private bool _disposed;
    private readonly object _lock = new();  // socket 线程安全锁
    private byte[] _recvBuf = new byte[512];
    public PodState State { get; } = new();
    public DeviceCapabilities Caps { get; private set; } = DeviceCapabilities.Detect(null);

    public event Action? StateChanged;
    public string? LastError { get; private set; }

    public bool IsConnected => State.Connected;

    // WinSock2 imports
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort version, IntPtr data);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr s, IntPtr addr, int addrLen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(IntPtr s);

    private const int AF_BTH = 32;
    private const int SOCK_STREAM = 1;
    private const int BTHPROTO_RFCOMM = 3;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort family;
        public ulong btAddr;
        public Guid serviceClassId;
        public uint port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSAData
    {
        public ushort version;
        public ushort highVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string systemStatus;
        public ushort maxSockets;
        public ushort maxUdpDg;
        public IntPtr vendorInfo;
    }

    public async Task ConnectAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var wsaPtr = Marshal.AllocHGlobal(400);
                WSAStartup(0x0202, wsaPtr);
                Marshal.FreeHGlobal(wsaPtr);

                var (addr, name) = ReadBtDevice();
                if (addr == 0)
                {
                    LastError = "未找到已配对的 OPPO 设备";
                    return;
                }

                Caps = DeviceCapabilities.Detect(name ?? $"耳机 ({addr:X12})");

                _socket = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
                if (_socket == IntPtr.Zero)
                {
                    LastError = "无法创建 BTH socket";
                    return;
                }

                // 尝试多种方式连接，按优先级排列
                if (TryConnect(addr, OppoProtocol.OppoSppUuid, 0))   // 方式1：UUID + SDP 自动解析
                    goto connected;
                if (TryConnect(addr, OppoProtocol.OppoSppUuid, 15))  // 方式2：UUID + 默认端口 15
                    goto connected;
                if (TryConnect(addr, Guid.Empty, 15))                 // 方式3：空 UUID + 默认端口 15
                    goto connected;
                if (TryConnect(addr, Guid.Empty, 1))                  // 方式4：尝试端口 1
                    goto connected;

                // 都失败
                closesocket(_socket);
                _socket = IntPtr.Zero;
                LastError = "无法连接耳机（已尝试 4 种连接方式）";
                return;

            connected:
                State.Connected = true;
                LastError = null;

                // 五连发 + 一次长读
                Send(OppoProtocol.PktBatchQuery);
                Thread.Sleep(80);
                Send(OppoProtocol.PktBattery);
                Thread.Sleep(80);
                Send(OppoProtocol.PktQueryAnc);
                Thread.Sleep(80);
                Send(OppoProtocol.PktQueryEq);
                Thread.Sleep(80);
                Send(OppoProtocol.PktRegisterNotify);
                Thread.Sleep(80);
                Send(OppoProtocol.PktMultiConnectInfo);
                Thread.Sleep(500);
                ReadResponses(3000);
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
        });
    }

    /// <summary>尝试通过指定参数建立 BTH RFCOMM 连接</summary>
    private bool TryConnect(ulong btAddr, Guid serviceUuid, uint port)
    {
        var sa = new SockAddrBth
        {
            family = AF_BTH,
            btAddr = btAddr,
            serviceClassId = serviceUuid,
            port = port
        };
        int saSize = Marshal.SizeOf(sa);
        var saPtr = Marshal.AllocHGlobal(saSize);
        Marshal.StructureToPtr(sa, saPtr, false);

        int result = connect(_socket, saPtr, saSize);
        Marshal.FreeHGlobal(saPtr);

        return result == 0;
    }

    private (ulong addr, string? name) ReadBtDevice()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            if (key == null) return (0, null);

            // 第一轮：按名称匹配 "OPPO"
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length != 12 || !ulong.TryParse(subName,
                    System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    continue;

                string? name = ReadBtDeviceName(key, subName);

                if (!string.IsNullOrEmpty(name) && name.Contains("OPPO", StringComparison.OrdinalIgnoreCase))
                    return (addr, name);
            }

            // 第二轮：按服务 UUID 匹配（即使名称不含 "OPPO" 也能找到）
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length != 12 || !ulong.TryParse(subName,
                    System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    continue;

                if (HasOppoSppService(key, subName))
                {
                    var name = ReadBtDeviceName(key, subName);
                    return (addr, name);
                }
            }

            // 回退：没找到 OPPO 名称或 UUID 的，返回第一个 BT 地址
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length == 12 && ulong.TryParse(subName,
                    System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    return (addr, null);
            }
        }
        catch { }
        return (0, null);
    }

    /// <summary>从注册表读取蓝牙设备名称</summary>
    private static string? ReadBtDeviceName(RegistryKey devicesKey, string subKeyName)
    {
        try
        {
            using var devKey = devicesKey.OpenSubKey(subKeyName);
            if (devKey == null) return null;

            var raw = devKey.GetValue("Name");
            var name = raw switch
            {
                byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
                string s => s,
                _ => null
            };

            if (string.IsNullOrEmpty(name))
            {
                var fn = devKey.GetValue("FriendlyName");
                name = fn switch
                {
                    string s => s,
                    byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
                    _ => null
                };
            }

            return name;
        }
        catch { return null; }
    }

    /// <summary>检查注册表中是否有 OPPO SPP 服务的 SDP 记录</summary>
    private static bool HasOppoSppService(RegistryKey devicesKey, string subKeyName)
    {
        try
        {
            // Windows 存储 SDP 记录的路径
            using var sdpKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{subKeyName}");
            if (sdpKey == null) return false;

            // OPPO SPP UUID: 0000079A-D102-11E1-9B23-00025B00A5A5
            // 注册表中服务子键名称为 UUID 去横线大写: 0000079AD10211E19B2300025B00A5A5
            foreach (var serviceName in sdpKey.GetSubKeyNames())
            {
                if (serviceName.Contains("0000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (serviceName.Contains("000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch { return false; }
    }

    private void ReadResponses(int timeoutMs)
    {
        if (_socket == IntPtr.Zero) return;
        var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var framer = new List<byte>();

        while (DateTime.UtcNow < endTime)
        {
            int got = recv(_socket, _recvBuf, _recvBuf.Length, 0);
            if (got <= 0) throw new Exception("连接已断开");  // 对端关闭或 socket 错误

            for (int i = 0; i < got; i++)
            {
                framer.Add(_recvBuf[i]);
                // Try to parse frames as they accumulate
                while (TryExtractFrame(framer, out var frame))
                    ParseFrame(frame);
            }
        }
    }

    private bool TryExtractFrame(List<byte> buffer, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        int start = buffer.IndexOf(0xAA);
        if (start < 0) { buffer.Clear(); return false; }
        if (start > 0) buffer.RemoveRange(0, start);
        if (buffer.Count < 2) return false;

        int totalLen = buffer[1];
        int frameLen = totalLen + 2;
        if (totalLen < 7 || frameLen > 512) { buffer.RemoveAt(0); return false; }
        if (buffer.Count < frameLen) return false;

        frame = buffer.GetRange(0, frameLen).ToArray();
        buffer.RemoveRange(0, frameLen);
        return true;
    }

    private void ParseFrame(byte[] pkt)
    {
        if (pkt.Length < 9) return;
        ushort cmd = (ushort)(pkt[4] | (pkt[5] << 8));
        int payLen = pkt[7] | (pkt[8] << 8);
        int payloadStart = 9;

        switch (cmd)
        {
            case OppoProtocol.CmdBatteryResp:
                ParseBattery(pkt, payloadStart, payLen);
                break;
            case OppoProtocol.CmdAncResp:
                ParseAnc(pkt, payloadStart, payLen);
                break;
            case OppoProtocol.CmdActiveReport:
                ParseActiveReport(pkt, payloadStart, payLen);
                break;
            case OppoProtocol.CmdEqResp:
            case OppoProtocol.CmdEqNotify:
                ParseEq(pkt, payloadStart, payLen);
                break;
            case OppoProtocol.CmdBatchQueryResp:
                ParseBatchStatus(pkt, payloadStart, payLen);
                break;
            case OppoProtocol.CmdMultiConnectResp:
                ParseMultiConnect(pkt, payloadStart, payLen);
                break;
        }
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
        for (int i = 0; i + 3 < len; i++)
        {
            if (pkt[start + i] == 0x01 && pkt[start + i + 1] == 0x01)
            {
                byte v1 = pkt[start + i + 2], v2 = pkt[start + i + 3];
            if (OppoProtocol.AncValues.TryGetValue((v1, v2), out var mode))
            {
                // 旧版 ANC 值交换（解析时换回来）
                State.AncMode = Caps.IsLegacyAnc ? OppoProtocol.LegacyAncSwap(mode) : mode;
            }
            }
        }
        StateChanged?.Invoke();
    }

    private void ParseActiveReport(byte[] pkt, int start, int len)
    {
        if (len < 2) return;
        int reportType = pkt[start];
        if (reportType == 0x01)
        {
            int count = pkt[start + 1];
            for (int j = 0; j < count && start + 2 + j * 2 + 1 < start + len; j++)
            {
                int idx = pkt[start + 2 + j * 2];
                int raw = pkt[start + 2 + j * 2 + 1];
                int level = raw & 0x7F;
                bool charging = (raw & 0x80) != 0;
                var key = idx switch { 1 => "L", 2 => "R", 3 => "C", _ => null };
                if (key != null) State.Battery[key] = (level, charging);
            }
        }
        else if (reportType == 0x02)
        {
            int count = pkt[start + 1];
            for (int j = 0; j < count && start + 2 + j * 2 + 1 < start + len; j++)
            {
                int comp = pkt[start + 2 + j * 2];
                int st = pkt[start + 2 + j * 2 + 1];
                string status = st switch
                {
                    0 => "已断连", 4 => "入盒", 5 => "摘下", 7 => "佩戴", _ => "?"
                };
                if (comp == 1) State.WearingL = status;
                else if (comp == 2) State.WearingR = status;
            }
        }
        StateChanged?.Invoke();
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
            System.Diagnostics.Debug.WriteLine("[BT] ParseMultiConnect: len=" + len + ", full=" + BitConverter.ToString(pkt, start, Math.Min(len, 48)));
            var devices = new List<ConnectedDeviceInfo>();
            if (len < 2) return;

            int count = pkt[start + 1];
            if (count <= 0 || count > 8)
            {
                System.Diagnostics.Debug.WriteLine("[BT]  invalid count=" + count);
                return;
            }

            int pos = start + 2;
            for (int i = 0; i < count && pos + 8 < start + len; i++)
            {
                // Format: [addr(6)] [state(1)] [type(1)] [reserved(1)] [nameLen(1)] [name(nameLen)]
                var addr = string.Join(":", Enumerable.Range(0, 6).Select(j => pkt[pos + j].ToString("X2")));
                pos += 6;

                int stateFlags = pkt[pos++];   // bitmask: bit3=current, bit2=mainAudio, bit1=audioActive, bit0=connected?
                int connState = pkt[pos++];    // 0=disconnected, 2=connected
                int reserved = pkt[pos++];
                int nameLen = pkt[pos++];

                if (nameLen < 0 || pos + nameLen > start + len)
                {
                    System.Diagnostics.Debug.WriteLine("[BT]  MultiDevice [" + i + "]: invalid nameLen=" + nameLen);
                    break;
                }

                string deviceName = nameLen > 0
                    ? System.Text.Encoding.UTF8.GetString(pkt, pos, nameLen).TrimEnd("\0".ToCharArray())
                    : "Device " + addr.Substring(Math.Max(0, addr.Length - 5));
                pos += Math.Max(nameLen, 0);

                bool isCurrent = (stateFlags & 0x08) != 0;
                bool isAudioActive = (stateFlags & 0x02) != 0;
                bool isMainAudio = (stateFlags & 0x04) != 0;

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
                System.Diagnostics.Debug.WriteLine("[BT]  MultiDevice [" + i + "]: addr=" + addr + ", name=\"" + deviceName + "\", connState=" + connState + ", flags=0x" + stateFlags.ToString("X2") + ", cur=" + isCurrent);
            }

            if (devices.Count > 0)
            {
                // 当前设备排最前
                devices = devices.OrderByDescending(d => d.IsCurrentDevice).ThenBy(d => d.DeviceName).ToList();
                State.ConnectedDevices = devices;
                State.MultiConnectListUpdatedAt = DateTime.Now;
                System.Diagnostics.Debug.WriteLine("[BT] Multi-device list updated: " + devices.Count + " devices, names: " + string.Join(", ", devices.Select(d => d.DeviceName)));
                StateChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[BT] ParseMultiConnect error: " + ex.Message);
        }
    }

    /// <summary>主动查询多设备连接列表</summary>
    public void SendMultiConnectInfo() => Send(OppoProtocol.PktMultiConnectInfo);

    /// <summary>切换多设备中的活动设备</summary>
    public void SendOperateHandheld(string targetAddress, bool connect = true)
    {
        // cmd 0x0429: [操作类型(1), 地址(6)]
        var addrBytes = targetAddress.Split(':').Select(b => Convert.ToByte(b, 16)).ToArray();
        var payload = new byte[1 + addrBytes.Length];
        payload[0] = (byte)(connect ? 0x01 : 0x00);
        Buffer.BlockCopy(addrBytes, 0, payload, 1, addrBytes.Length);
        Send(OppoProtocol.BuildPacket(OppoProtocol.CmdOperateHandheld, payload));
    }

    public void SendAnc(string mode)
    {
        // 旧版 ANC 值交换
        if (Caps.IsLegacyAnc)
            mode = OppoProtocol.LegacyAncSwap(mode);
        Send(OppoProtocol.PktAncMode(mode));
    }
    public void SendBattery() => Send(OppoProtocol.PktBattery);
    public void SendSpatial(bool on) => Send(OppoProtocol.BuildFeaturePacket(OppoProtocol.FeatureSpatial, on));
    public void SendSpatialAudio(string mode) => Send(OppoProtocol.PktSpatialAudio(mode));
    public void SendDualDevice(bool on) => Send(OppoProtocol.BuildFeaturePacket(OppoProtocol.FeatureDualDevice, on));
    public void SendGameMode(bool on, bool compatible = false)
    {
        // 标准: 只发 28; 兼容: 发 28 + 06 (顺序执行, 无阻塞延迟)
        Send(OppoProtocol.BuildFeaturePacket(OppoProtocol.FeatureGameMain, on));
        if (compatible)
            Send(OppoProtocol.BuildFeaturePacket(OppoProtocol.FeatureGameLL, on));
    }
    public void SendEq(string name)
    {
        if (Caps.EqPresets.TryGetValue(name, out var id))
            Send(OppoProtocol.BuildPacket(OppoProtocol.CmdSetEq, new[] { id }));
    }

    public async Task PollAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            int tick = 0;
            int intervalMs = 2000;  // 初始 2 秒快速同步
            while (!ct.IsCancellationRequested && State.Connected)
            {
                try
                {
                    // 电池和 ANC 每轮都查（物理按键切换需快速响应）
                    SendBattery();
                    Thread.Sleep(100);
                    Send(OppoProtocol.PktQueryAnc);
                    Thread.Sleep(100);
                    ReadResponses(500);
                    tick++;

                    if (tick % 6 == 0)  // 每 6 轮查 EQ
                    {
                        Send(OppoProtocol.PktQueryEq);
                        Thread.Sleep(100);
                        ReadResponses(400);
                    }

                    if (tick % 4 == 0)  // 每 4 轮查功能状态（游戏模式/双设备/空间音效）
                    {
                        Send(OppoProtocol.PktBatchQuery);
                        Thread.Sleep(100);
                        ReadResponses(400);
                    }

                    if (tick % 4 == 0)  // 每 4 轮查多设备连接列表（与功能状态同步）
                    {
                        Send(OppoProtocol.PktMultiConnectInfo);
                        Thread.Sleep(100);
                        ReadResponses(400);
                    }

                    // 前 10 轮 2s，之后放缓到 5s
                    if (tick == 10) intervalMs = 5000;
                    Thread.Sleep(intervalMs);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    break;
                }
            }
            State.Connected = false;
            StateChanged?.Invoke();
        }, ct);
    }

    private void Send(byte[] pkt)
    {
        lock (_lock)
        {
            if (_socket != IntPtr.Zero)
                send(_socket, pkt, pkt.Length, 0);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            State.Connected = false;
            if (_socket != IntPtr.Zero)
            {
                closesocket(_socket);
                _socket = IntPtr.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        WSACleanup();
        _disposed = true;
    }
}




