using System;
using System.Collections.Generic;
using System.Linq;

namespace OppoPodsManager;

/// <summary>帧响应解析器（PodManager 的 partial 部分）：所有 Parse* 方法汇总于此。</summary>
public partial class PodManager
{
    /// <summary>从帧载荷截取一段，统一成独立数组给解析器。</summary>
    private static byte[] Slice(byte[] pkt, int start, int len)
    {
        if (len <= 0) return Array.Empty<byte>();
        var b = new byte[len];
        Array.Copy(pkt, start, b, 0, len);
        return b;
    }

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
            RebuildCapabilitySet();
            StateChanged?.Invoke();
        }
    }

    private void ParseBattery(byte[] pkt, int start, int len)
    {
        Log.D("RFCOMM", $"ParseBattery: len={len} raw={BitConverter.ToString(pkt, start, Math.Min(len, 20))}");
        // SPP fixed-offset format: [00, 04, L_lev, L_chg, R_lev, R_chg, C_lev, C_chg, ...]
        if (len >= 8 && pkt[start] == 0x00 && pkt[start+1] == 0x04)
        {
            State.Battery["L"] = (pkt[start+2], pkt[start+3] != 0);
            State.Battery["R"] = (pkt[start+4], pkt[start+5] != 0);
            State.Battery["C"] = (pkt[start+6], pkt[start+7] != 0);
            Log.D("RFCOMM", $"ParseBattery(SPP): L={pkt[start+2]}/{pkt[start+3]} R={pkt[start+4]}/{pkt[start+5]} C={pkt[start+6]}/{pkt[start+7]}");
            StateChanged?.Invoke();
            return;
        }
        // GATT/melody list format: [idx, raw, idx, raw, ...]
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
        if (len >= 5 && pkt[start + 1] == 0x04)
        {
            var rt = ParseNoiseBitmap(pkt, start + 2, len - 2);
            State.IntelligentRealtime = (rt != null && rt != "Smart") ? rt : "";
            Log.D("RFCOMM", $"ParseAnc: 智能实时(查询) -> {(string.IsNullOrEmpty(State.IntelligentRealtime) ? "(无)" : State.IntelligentRealtime)}");
            StateChanged?.Invoke();
            return;
        }

        for (int i = 0; i + 3 < len; i++)
        {
            if (pkt[start + i] == 0x01 && pkt[start + i + 1] == 0x01)
            {
                byte v1 = pkt[start + i + 2], v2 = pkt[start + i + 3];

                if (Caps.AncIndexToName.Count > 0)
                {
                    int value = v1 + v2 * 256;
                    int bit = 1;
                    for (int idx = 0; idx < 16; idx++)
                    {
                        if ((value & bit) != 0 && Caps.AncIndexToName.TryGetValue((byte)idx, out var name))
                        {
                            State.AncMode = name;
                            if (name != "Smart") State.IntelligentRealtime = "";
                            break;
                        }
                        bit *= 2;
                    }
                }
                else if (OppoProtocol.AncValues.TryGetValue((v1, v2), out var mode))
                {
                    State.AncMode = Caps.IsLegacyAnc ? OppoProtocol.LegacyAncSwap(mode) : mode;
                }
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>解析 0x0204 主动通知事件。</summary>
    private void ParseActiveReport(byte[] pkt, int start, int len)
    {
        if (len < 1) return;
        int subType = pkt[start];
        int bodyStart = start + 1;
        int bodyLen = len - 1;
        Log.D("RFCOMM", $"ParseActiveReport: subType=0x{subType:X2}({OppoProtocol.ActiveReportName(subType)}) len={len}");

        switch (subType)
        {
            case OppoProtocol.EvtBattery:
                ParseBatteryList(pkt, bodyStart, bodyLen);
                break;
            case OppoProtocol.EvtEarBudsStatus:
                ParseWearingData(pkt, start, len);
                break;
            case OppoProtocol.EvtNoiseMode:
                ParseNoiseChange(pkt, bodyStart, bodyLen);
                break;
            case OppoProtocol.EvtGameMode:
                if (bodyLen >= 1)
                {
                    State.GameMode = pkt[bodyStart] != 0;
                    Log.D("RFCOMM", $"ParseActiveReport: 游戏模式 -> {State.GameMode}");
                }
                break;
            case OppoProtocol.EvtZenMode:
                if (bodyLen >= 1)
                    Log.D("RFCOMM", $"ParseActiveReport: 禅模式 -> {pkt[bodyStart]}");
                break;
            case OppoProtocol.EvtMultiConnect:
                Log.D("RFCOMM", "ParseActiveReport: 多连接状态变更，刷新列表");
                _transport.Send(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty);
                break;
            case OppoProtocol.EvtCompactness:
            case OppoProtocol.EvtHearingDetect:
            case OppoProtocol.EvtCodecType:
            case OppoProtocol.EvtPersonalNoise:
            case OppoProtocol.EvtTriangle:
            case OppoProtocol.EvtEarScan:
            case OppoProtocol.EvtGaming:
            case OppoProtocol.EvtOneshot:
            case OppoProtocol.EvtToneChange:
                break;
            default:
                Log.D("RFCOMM", $"ParseActiveReport: 未识别子类型 0x{subType:X2}");
                break;
        }
        StateChanged?.Invoke();
    }

    private void ParseBatteryList(byte[] pkt, int start, int len)
    {
        Log.D("RFCOMM", $"ParseBatteryList: len={len} raw={BitConverter.ToString(pkt, start, Math.Min(len, 20))}");
        // SPP fixed-offset format: [00, 04, L_lev, L_chg, R_lev, R_chg, C_lev, C_chg, ...]
        if (len >= 8 && pkt[start] == 0x00 && pkt[start+1] == 0x04)
        {
            State.Battery["L"] = (pkt[start+2], pkt[start+3] != 0);
            State.Battery["R"] = (pkt[start+4], pkt[start+5] != 0);
            State.Battery["C"] = (pkt[start+6], pkt[start+7] != 0);
            Log.D("RFCOMM", $"ParseBatteryList(SPP): L={pkt[start+2]}/{pkt[start+3]} R={pkt[start+4]}/{pkt[start+5]} C={pkt[start+6]}/{pkt[start+7]}");
            StateChanged?.Invoke();
            return;
        }
        if (len < 2) return;
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

    private void ParseNoiseChange(byte[] pkt, int start, int len)
    {
        if (len < 1) return;
        int kind = pkt[start];
        int infoStart = start + 1;
        int infoLen = len - 1;

        if (kind == 1)
        {
            var name = ParseNoiseBitmap(pkt, infoStart, infoLen);
            if (name != null)
            {
                State.AncMode = name;
                State.IntelligentRealtime = "";
                Log.D("RFCOMM", $"ParseNoiseChange: 手动 ANC -> {name}");
            }
        }
        else if (kind == 4)
        {
            var name = ParseNoiseBitmap(pkt, infoStart, infoLen);
            if (name != null)
            {
                State.AncMode = "Smart";
                State.IntelligentRealtime = name;
                Log.D("RFCOMM", $"ParseNoiseChange: 智能实时 -> {name}");
            }
        }
        else
        {
            _transport.Send(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAnc);
        }
        StateChanged?.Invoke();
    }

    private string? ParseNoiseBitmap(byte[] pkt, int start, int len)
    {
        if (len < 2 || Caps.AncIndexToName.Count == 0) return null;
        int mType = pkt[start];
        if (mType != 1) return null;

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
        bool leftDone = false, rightDone = false;
            _ = leftDone; _ = rightDone;
        for (int j = 0; j < count && start + 2 + j * 2 + 1 < start + len; j++)
        {
            int comp = pkt[start + 2 + j * 2];
            int st = pkt[start + 2 + j * 2 + 1];
            string status = st switch
            {
                0 => "已断连", 1 => "摘下", 3 => "佩戴", 4 => "入盒", 5 => "摘下", 7 => "佩戴", _ => "?" + st
            };
            if (comp == 1) { State.WearingL = status; if (st == 0) leftDone = true; }
            else if (comp == 2) { State.WearingR = status; if (st == 0) rightDone = true; }
        }
        Log.D("RFCOMM", $"ParseWearingData: L='{State.WearingL}' R='{State.WearingR}'");
        // 佩戴状态仅用于 UI 显示，不再触发断连
        // 不同型号的 st=0 语义不同：OnePlus 为"已断连"，Enco 为"未佩戴"
        // 统一用 idle timeout 检测真实断连
    }

    private void ParseEq(byte[] pkt, int start, int len)
    {
        if (len >= 2)
        {
            byte eqId = pkt[start + 1];
            // 优先用 JSON 内置名称，降级到设备端名称，再降级到 "?"
            var name = Caps.EqNames.GetValueOrDefault(eqId);
            if (name == null) name = Caps.DeviceEqNames.GetValueOrDefault(eqId);
            State.EqPreset = name ?? "?";
        }
        StateChanged?.Invoke();
    }

    private void ParseEqAll(byte[] pkt, int start, int len)
    {
        try
        {
            var payload = Slice(pkt, start, len);
            var entries = OppoProtocol.ParseEqAll(payload);
            if (entries.Count == 0) { Log.D("RFCOMM", "ParseEqAll: 解析到 0 个条目"); return; }

            var now = DateTime.Now;
            foreach (var expired in _pendingDeleteEqIds.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                _pendingDeleteEqIds.Remove(expired);

            // 按 _pendingDeleteEqIds 过滤已发送删除命令但设备短时间内仍回读旧状态的条目。
            // Melody 删除后立即移除 UI，并保持约 1 秒删除追踪窗口；这里不要在第一次过滤后清空。
            List<EqInfoEntry> valid;
            if (_pendingDeleteEqIds.Count > 0)
            {
                valid = entries.Where(e => !_pendingDeleteEqIds.ContainsKey(e.EqId)).ToList();
                var before = entries.Count;
                var filtered = before - valid.Count;
                if (filtered > 0)
                {
                    var ids = string.Join(",", _pendingDeleteEqIds.Keys.Select(id => id.ToString()));
                    Log.D("RFCOMM", $"ParseEqAll: 删除追踪窗口内过滤 {filtered} 个旧条目 (ids=[{ids}]), 剩余 {valid.Count} 个");
                }
                if (valid.Count == 0) { Log.D("RFCOMM", "ParseEqAll: 过滤后无有效条目"); return; }
            }
            else
                valid = entries.ToList();

            // 提取设备端名称（eqId → name），补充/覆盖 JSON 内置 EqNames
            var deviceNames = new Dictionary<byte, string>();
            foreach (var e in valid)
                if (!deviceNames.ContainsKey(e.EqId))
                    deviceNames[e.EqId] = e.Name;

            Caps.DeviceEqNames = deviceNames;
            State.DeviceEqEntries = valid;

            // 若当前选中的预设 eqId 匹配设备端某条目且设备端有名称，更新显示
            foreach (var e in valid)
            {
                if (e.IsSelected && !string.IsNullOrEmpty(e.Name))
                {
                    State.EqPreset = e.Name;
                    break;
                }
            }

            Log.D("RFCOMM", $"ParseEqAll: {entries.Count} 个预设, 设备名={(deviceNames.Count > 0 ? string.Join(",", deviceNames) : "无")}");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Ex("RFCOMM", "ParseEqAll", ex);
        }
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
                // 设备按小端(倒序)传 MAC：wire 首字节是 MAC 末字节。melody HandheldDeviceInfo.parseAddress
                // 同样倒序还原成 AA:BB:CC:DD:EE:FF 显示序。这里必须反转，否则存下来的地址是真地址的字节倒序，
                // 回发操作命令(0x0429)时目标对不上 → 设备回 ACK 成功但实际没断开（本次 bug 根因）。
                var addr = string.Join(":", Enumerable.Range(0, 6).Select(j => pkt[pos + 5 - j].ToString("X2")));
                pos += 6;

                int elemByte6 = pkt[pos++];
                int connState = pkt[pos++];
                int flag = pkt[pos++];
                int nameLen = pkt[pos++];

                if (nameLen < 0 || pos + nameLen > start + len)
                {
                    Log.D("RFCOMM", "ParseMultiConnect: device[" + i + "] invalid nameLen=" + nameLen);
                    break;
                }

                string deviceName = nameLen > 0
                    ? System.Text.Encoding.UTF8.GetString(pkt, pos, nameLen).TrimEnd("\0".ToCharArray())
                    : "Device " + addr.Substring(Math.Max(0, addr.Length - 5));
                pos += Math.Max(nameLen, 0);

                bool isCurrent = (flag & 0x01) != 0;
                bool isMainAudio = (flag & 0x02) != 0;
                bool isAudioActive = (flag & 0x04) != 0;

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

    private void ParseMultiPriority(byte[] pkt, int start, int len)
    {
        try
        {
            Log.D("RFCOMM", "ParseMultiPriority: len=" + len + ", data=" + BitConverter.ToString(pkt, start, Math.Min(len, 24)));
            if (len < 3) return;
            if (pkt[start] != 0) return;

            int level = pkt[start + 1];
            bool isHighLevel = level == 2;
            byte modeByte = pkt[start + 2];
            bool autoMode = false;
            string priorityAddr = "";

            if (isHighLevel)
            {
                autoMode = modeByte == 0;
                if (!autoMode && len >= 9)
                {
                    // MAC 小端倒序，反转成显示序（与 ParseMultiConnect 一致）
                    priorityAddr = string.Join(":",
                        Enumerable.Range(0, 6).Select(j => pkt[start + 3 + 5 - j].ToString("X2")));
                }
            }
            else
            {
                if (modeByte != 0 && len >= 9)
                {
                    priorityAddr = string.Join(":",
                        Enumerable.Range(0, 6).Select(j => pkt[start + 3 + 5 - j].ToString("X2")));
                }
            }

            State.MultiConnectAutoMode = autoMode;
            State.PriorityDeviceAddress = priorityAddr;
            Log.D("RFCOMM", $"ParseMultiPriority: level={level} high={isHighLevel} auto={autoMode} addr=\"{priorityAddr}\"");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Ex("RFCOMM", "ParseMultiPriority", ex);
        }
    }
}
