using System;
using System.Collections.Generic;
using OppoPodsManager.Localization;

namespace OppoPodsManager.Models;

/// <summary>
/// 用户交互事件信息（0x0204 subType=0xF1 的数据体）。
///
/// 来源：Melody APK h.b() packed-switch offset -0xf → UserInteractionEventInfo(data, offset)。
/// payload = [byte0(1B), byte1(1B), byte2(1B), byte3(1B), byte4(1B), Options(variable int16[])]
///

/// </summary>
public class UserInteractionEventInfo
{
    /// <summary>字节 0：疑似设备/声道标识。0x01=左耳?, 0x02=右耳(实测确认), 0x03=?</summary>
    public byte Byte0 { get; }
    /// <summary>字节 1：按键/区域 ID（型号相关，Enco Air5 Pro 长按=0x06）。</summary>
    public byte Byte1 { get; }
    /// <summary>字节 2：按键动作。0x00=单击, 0x04=长按（实测确认），其余待验证。</summary>
    public byte Byte2 { get; }
    /// <summary>字节 3：功能位/修饰符（长按时有 0x08 出现）。</summary>
    public byte Byte3 { get; }
    /// <summary>字节 4：场景/上下文。</summary>
    public byte Byte4 { get; }
    public List<int> Options { get; } = new();

    public UserInteractionEventInfo(byte[] payload, int start, int length)
    {
        if (length < 5) return;

        Byte0 = payload[start];
        Byte1 = payload[start + 1];
        Byte2 = payload[start + 2];
        Byte3 = payload[start + 3];
        Byte4 = payload[start + 4];

        int pos = start + 5;
        while (pos + 1 < start + length)
        {
            int opt = payload[pos] | (payload[pos + 1] << 8);
            Options.Add(opt);
            pos += 2;
        }
    }

    /// <summary>Byte0 → 设备侧名称（实测确认：0x02=右耳）。</summary>
    public static string SideName(byte v) => v switch
    {
        0x01 => LanguageManager.Instance.GetString(LanguageManager.Instance.Battery_Left),
        0x02 => LanguageManager.Instance.GetString(LanguageManager.Instance.Battery_Right),
        _ => string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_UnknownSide), v)
    };

    /// <summary>
    /// Byte2 → 动作名。
    /// 来源：实测 Enco Air5 Pro：
    ///   0x00=单击 0x02=双击(DOUBLE_CLICK, JADX 确认) 0x03=三击
    ///   0x04=长按 0x07=上滑 0x08=下滑
    /// </summary>
    public static string ActionName(byte v) => v switch
    {
        0x00 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_SingleTap),
        0x02 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_DoubleTap),
        0x03 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_TripleTap),
        0x04 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_LongPress),
        0x07 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_SlideUp),
        0x08 => LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_SlideDown),
        _ => string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_UnknownAction), v)
    };

    /// <summary>可读描述，如："右耳 按键(0x06): 长按"。</summary>
    public string Description =>
        string.Format("{0} {1}(0x{2:X2}): {3}",
            SideName(Byte0),
            LanguageManager.Instance.GetString(LanguageManager.Instance.DeviceInfo_Button),
            Byte1, ActionName(Byte2)) +
        (Byte3 != 0 ? $" +0x{Byte3:X2}" : "") +
        (Byte4 != 0 ? $" ctx=0x{Byte4:X2}" : "") +
        (Options.Count > 0 ? $" opts=[{string.Join(",", Options)}]" : "");
}
