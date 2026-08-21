using System;

namespace OppoPodsManager.Control.Brands.Edifier;

// 漫步者（Edifier）TWS / 头戴耳机的私有协议常量。
//
// 协议来源：mEDIFIER（开源，https://github.com/wh201906/mEDIFIER）及其分支 Klinkore-mEDIFIER。
// 主分支走 BLE GATT；Klinkore 分支额外提供 RFCOMM（SPP）通道，固定 SPP 服务 UUID 如下。
// 本适配复用项目的 WindowsRfcommConnection（RFCOMM），因此需要 SPP 通道的设备（W820NB 系列、
// W200BT 系列等）可直接连接；纯 BLE 设备需要额外 BLE 传输层，当前未实现。
//
// 许可证提示：mEDIFIER 仓库采用 GPL-3.0，正式发布前需确认 next 项目的许可证兼容。
public static class EdifierConstants
{
    // Klinkore 分支硬编码的 RFCOMM SPP 服务 UUID。
    public static readonly Guid EdifierSppServiceId = new("EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF");

    // 帧头与校验种子（addChecksum: sum = 8217 + 所有前导字节）。
    public const byte RequestHead = 0xAA;
    public const byte ResponseHeadBb = 0xBB;
    public const byte ResponseHeadCc = 0xCC;
    public const ushort ChecksumSeed = 8217; // 0x2019

    // ---- 命令字节（请求与响应回显同一命令字节）----
    // 电量查询：请求/响应命令均为 0xD0，响应 payload[0] = 电量百分比。
    public const byte QueryBattery = 0xD0;
    public const byte ReportBattery = 0xD0;

    // 降噪查询：请求/响应均为 0xCC，响应 payload = [mode, ambientVolume?]。
    public const byte QueryNoiseMode = 0xCC;
    public const byte ReportNoiseMode = 0xCC;

    // 降噪设置：请求/响应均为 0xC1，payload = [mode]。
    public const byte SetNoiseMode = 0xC1;
    public const byte AckNoiseMode = 0xC1;

    // ---- 漫步者降噪模式字节（对应 basedevice.cpp processData 的 CC 响应）----
    // 1=普通(关闭)，2=降噪(ANC)，3=通透(环境声)。
    public const byte NoiseOff = 1;
    public const byte NoiseAnc = 2;
    public const byte NoiseTransparency = 3;
}
