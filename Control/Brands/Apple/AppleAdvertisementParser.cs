namespace OppoPodsManager.Control.Brands.Apple;

// 解析 AirPods BLE 明文厂商数据（Apple 0x004C，data 不含 2 字节厂商 ID，与 Android
// ScanRecord.getManufacturerSpecificData / WinRT BluetoothLEManufacturerData.Data 一致）。
// 字节布局移植自 librepods BLEManager.parseProximityMessage：data[2]=paired,
// data[3..4]=型号(big-endian), data[5]=状态位, data[6]=左右耳电量 nibble,
// data[7]=充电盒电量 nibble + 充电标志, data[8]=盒盖, data[9]=颜色, data[10]=连接态。
// 加密广播(末 16 字节 AES)需 iPhone 配对密钥，PC 单独不可得，此处跳过。
internal static class AppleAdvertisementParser
{
    public static bool TryParse(ReadOnlySpan<byte> data, out AppleStatus status)
    {
        status = null!;
        // 参考实现要求长度 > 20 以排除加密变体；此处至少需要可读到第 10 字节。
        if (data.Length < 11)
            return false;

        var paired = data[2] == 1;
        var modelId = (ushort)(((data[3] & 0xFF) << 8) | (data[4] & 0xFF));
        var statusByte = data[5] & 0xFF;
        var podsBattery = data[6] & 0xFF;
        var flagsCase = data[7] & 0xFF;
        var lid = data[8] & 0xFF;

        var primaryLeft = ((statusByte >> 5) & 0x01) == 1;
        var thisInCase = ((statusByte >> 6) & 0x01) == 1;
        var xorFactor = primaryLeft ^ thisInCase;
        var isLeftInEar = xorFactor ? (statusByte & 0x08) != 0 : (statusByte & 0x02) != 0;
        var isRightInEar = xorFactor ? (statusByte & 0x02) != 0 : (statusByte & 0x08) != 0;
        var isFlipped = !primaryLeft;

        var leftNibble = isFlipped ? (podsBattery >> 4) & 0x0F : podsBattery & 0x0F;
        var rightNibble = isFlipped ? podsBattery & 0x0F : (podsBattery >> 4) & 0x0F;
        var caseNibble = flagsCase & 0x0F;
        var flags = (flagsCase >> 4) & 0x0F;
        var isLeftCharging = isFlipped ? (flags & 0x02) != 0 : (flags & 0x01) != 0;
        var isRightCharging = isFlipped ? (flags & 0x01) != 0 : (flags & 0x02) != 0;
        var isCaseCharging = (flags & 0x04) != 0;
        var lidOpen = ((lid >> 3) & 0x01) == 0;

        status = new AppleStatus(
            ModelName: AppleModels.TryGetName(modelId, out var name) ? name : null,
            Paired: paired,
            LeftBattery: DecodeBattery(leftNibble),
            RightBattery: DecodeBattery(rightNibble),
            CaseBattery: DecodeBattery(caseNibble),
            LeftCharging: isLeftCharging,
            RightCharging: isRightCharging,
            CaseCharging: isCaseCharging,
            LeftInEar: isLeftInEar,
            RightInEar: isRightInEar,
            LidOpen: lidOpen,
            Color: data.Length > 9 ? AppleModels.TryGetColor((byte)(data[9] & 0xFF)) : null,
            ConnectionState: null);
        return true;
    }

    // 电量 nibble：0x0–0x9 → n×10%，0xA–0xE → 100%，0xF → 无(null)。
    private static int? DecodeBattery(int nibble) => nibble switch
    {
        >= 0x0 and <= 0x9 => nibble * 10,
        >= 0xA and <= 0xE => 100,
        0xF => null,
        _ => null,
    };
}

// 一次 AirPods 广播解析出的只读状态。电量 null 表示该部件未佩戴/未知。
internal sealed record AppleStatus(
    string? ModelName,
    bool Paired,
    int? LeftBattery,
    int? RightBattery,
    int? CaseBattery,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    bool LeftInEar,
    bool RightInEar,
    bool LidOpen,
    string? Color,
    string? ConnectionState);
