namespace OppoPodsManager.Control.Brands.Apple;

// AirPods 型号识别与展示名映射。型号 id 取自 BLE 广播明文厂商数据的 data[3..4]
// (big-endian)，与 librepods BLEManager.modelNames 对齐。加密广播(需 iPhone 密钥)暂不支持，
// 因此仅依赖明文 id 解析；无法识别时返回 null，由 UI 显示通用 “AirPods”。
internal static class AppleModels
{
    private static readonly IReadOnlyDictionary<ushort, string> ModelNames = new Dictionary<ushort, string>
    {
        [0x0220] = "AirPods 1",
        [0x0A20] = "AirPods Max",
        [0x0E20] = "AirPods Pro",
        [0x0F20] = "AirPods 2",
        [0x1320] = "AirPods 3",
        [0x1420] = "AirPods Pro 2",
        [0x1920] = "AirPods 4",
        [0x1B20] = "AirPods 4 (ANC)",
        [0x1F20] = "AirPods Max (USB-C)",
        [0x2420] = "AirPods Pro 2 (USB-C)",
        // 以下型号补自 AirPodsDesktop GetModel（0x2027=Pro3、0x2012=Beats Fit Pro）
        // 与 OpenPods PodsStatus（idFull 为 big-endian 四字符，如 "2720"=Pro3）互证。
        [0x2720] = "AirPods Pro 3",
        [0x1220] = "Beats Fit Pro",
        [0x0B20] = "Powerbeats Pro",
        [0x0520] = "Beats X",
        [0x1020] = "Beats Flex",
        [0x0620] = "Beats Solo 3",
        [0x0920] = "Beats Studio 3",
        [0x0320] = "Powerbeats 3",
    };

    private static readonly IReadOnlyDictionary<byte, string> ColorNames = new Dictionary<byte, string>
    {
        [0x00] = "White", [0x01] = "Black", [0x02] = "Red", [0x03] = "Blue",
        [0x04] = "Pink", [0x05] = "Gray", [0x06] = "Silver", [0x07] = "Gold",
        [0x08] = "Rose Gold", [0x09] = "Space Gray", [0x0A] = "Dark Blue",
        [0x0B] = "Light Blue", [0x0C] = "Yellow",
    };

    public static bool TryGetName(ushort modelId, out string name)
        => ModelNames.TryGetValue(modelId, out name!);

    public static string? TryGetColor(byte color)
        => ColorNames.TryGetValue(color, out var c) ? c : null;
}
