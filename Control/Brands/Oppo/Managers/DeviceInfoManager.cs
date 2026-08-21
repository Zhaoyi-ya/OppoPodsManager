using System.Text;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Oppo.Managers;

// 解析产品标识、固件与编解码器等设备信息。
public sealed class DeviceInfoManager
{
    private readonly BusinessState _state;

    public DeviceInfoManager(BusinessState state)
    {
        _state = state;
    }

    public bool TryApplyProductId(ReadOnlySpan<byte> payload, string displayName, string? modelName = null)
    {
        if (payload.Length != 4 || payload[0] != 0)
            return false;

        var rawProductId = payload[1] | (payload[2] << 8) | (payload[3] << 16);
        var productId = NormalizeProductId(rawProductId).ToString("X6");
        _state.SetIdentity(new DeviceIdentity(productId, displayName, modelName, null, null));
        return true;
    }

    // 产品标识延迟上报时，先以蓝牙名称和型号库建立可显示的身份信息。
    public void ApplyFallbackIdentity(string displayName, string? modelName)
        => _state.SetIdentity(new DeviceIdentity(string.Empty, displayName, modelName, null, null));

    private static int NormalizeProductId(int productId) => productId switch
    {
        >= 0x100100 and <= 0x100102 => 0x060414,
        >= 0x100200 and <= 0x100202 => 0x060814,
        >= 0x108100 and <= 0x108102 => 0x068414,
        >= 0x108200 and <= 0x108202 => 0x068814,
        _ => productId
    };

    public void ApplyFirmware(ReadOnlySpan<byte> payload)
    {
        ApplicationLog.Current?.Debug("DeviceInfo", $"解析固件版本响应：bytes={payload.Length}。");
        var current = _state.Snapshot().Identity;
        if (current is null || payload.Length < 3 || payload[0] != 0)
            return;

        var firmware = Encoding.UTF8.GetString(payload[2..]).TrimEnd('\0', ' ');
        if (firmware.Contains(','))
            firmware = FormatFirmware(firmware);
        if (!string.IsNullOrEmpty(firmware))
        {
            ApplicationLog.Current?.Info("DeviceInfo", $"固件版本解析完成：version={firmware}。");
            _state.SetIdentity(current with { FirmwareVersion = firmware });
        }
    }

    // 将设备返回的设备类型、版本类型、版本号三元组转换为原项目显示格式。
    private static string FormatFirmware(string raw)
    {
        var parts = raw.Split(',');
        if (parts.Length < 3)
            return raw;

        var versions = new SortedDictionary<int, string>();
        for (var index = 0; index + 2 < parts.Length; index += 3)
        {
            if (int.TryParse(parts[index], out var deviceType)
                && int.TryParse(parts[index + 2], out var version))
                versions[deviceType] = version.ToString();
        }

        return versions.Count == 0 ? raw : string.Join('.', versions.Values);
    }

    public void ApplyCodec(ReadOnlySpan<byte> payload)
    {
        var current = _state.Snapshot().Identity;
        if (current is null || payload.Length < 2 || payload[0] != 0)
            return;

        var codec = payload.Length == 2 ? payload[1] : FindActiveCodec(payload);
        if (codec is not null)
            _state.SetIdentity(current with { Codec = CodecName(codec.Value) });
    }

    private static byte? FindActiveCodec(ReadOnlySpan<byte> payload)
    {
        var count = payload[1];
        if (count == 0 || payload.Length < 2 + count * 2)
            return null;

        for (var index = 0; index < count; index++)
        {
            if (payload[3 + index * 2] != 0)
                return payload[2 + index * 2];
        }

        return null;
    }

    private static string CodecName(byte codec) => codec switch
    {
        0 => "SBC", 1 => "LDAC", 2 => "AAC", 3 or 8 => "LHDC", 4 => "LC3",
        5 => "aptX", 6 => "aptX HD", 7 => "aptX Adaptive", _ => $"Codec {codec}"
    };
}
