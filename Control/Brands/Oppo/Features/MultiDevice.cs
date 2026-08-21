using System.Text;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Features;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析多设备列表和优先连接策略。
public sealed class MultiDevice
{
    private readonly BusinessState _state;

    public MultiDevice(BusinessState state)
    {
        _state = state;
    }

    public bool Apply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return false;

        var count = payload[1];
        var position = 2;
        var devices = new List<ConnectedDeviceSnapshot>(count);
        for (var index = 0; index < count; index++)
        {
            if (position + 10 > payload.Length)
                return false;

            var address = FormatAddress(payload.Slice(position, 6));
            position += 6;
            var type = payload[position++];
            var connectionState = payload[position++];
            var flags = payload[position++];
            var nameLength = payload[position++];
            if (position + nameLength > payload.Length)
                return false;

            // 名称缺失时保留空值，由界面层使用当前语言生成显示名称。
            var name = nameLength == 0
                ? string.Empty
                : Encoding.UTF8.GetString(payload.Slice(position, nameLength)).TrimEnd('\0');
            position += nameLength;
            devices.Add(new ConnectedDeviceSnapshot(
                address,
                name,
                type,
                connectionState,
                (flags & 0x01) != 0,
                (flags & 0x04) != 0,
                (flags & 0x02) != 0));
        }

        _state.SetMultiDevice(new MultiDeviceSnapshot(devices, true, null));
        return true;
    }

    public bool ApplyPriority(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return false;

        var offset = payload.Length >= 3 && payload[0] == 0 && (payload[1] == 1 || payload[1] == 2)
            ? 1
            : 0;
        if (payload.Length - offset < 2 || (payload[offset] != 1 && payload[offset] != 2))
            return false;

        var isAutomatic = payload[offset + 1] == 0;
        string? priorityAddress = null;
        if (!isAutomatic && payload.Length - offset >= 8)
            priorityAddress = FormatAddress(payload.Slice(offset + 2, 6));
        else if (!isAutomatic)
            isAutomatic = true;

        var current = _state.Snapshot().MultiDevice;
        _state.SetMultiDevice(current with { IsAutomaticPriority = isAutomatic, PriorityDeviceAddress = priorityAddress });
        return true;
    }

    public static bool TryBuildOperationPayload(
        MultiDeviceOperation operation,
        string? address,
        out byte[] payload)
    {
        payload = [];
        if (operation == MultiDeviceOperation.AutomaticPriority)
        {
            payload = [4, 0];
            return true;
        }

        if (!TryParseAddress(address, out var bytes))
            return false;

        if (operation == MultiDeviceOperation.SetPriority)
        {
            payload = new byte[8];
            payload[0] = 4;
            payload[1] = 1;
            bytes.CopyTo(payload, 2);
            return true;
        }

        payload = new byte[7];
        payload[0] = (byte)operation;
        bytes.CopyTo(payload, 1);
        return true;
    }

    private static string FormatAddress(ReadOnlySpan<byte> wireAddress)
    {
        var parts = new string[6];
        for (var index = 0; index < wireAddress.Length; index++)
            parts[5 - index] = wireAddress[index].ToString("X2");

        return string.Join(':', parts);
    }

    private static bool TryParseAddress(string? address, out byte[] bytes)
    {
        bytes = [];
        var parts = address?.Split(':');
        if (parts is null || parts.Length != 6)
            return false;

        bytes = new byte[6];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.AllowHexSpecifier, null, out bytes[index]))
                return false;
        }

        return true;
    }
}

