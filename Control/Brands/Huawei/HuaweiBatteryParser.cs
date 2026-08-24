using System.Text.RegularExpressions;
namespace OppoPodsManager.Control.Brands.Huawei;
// 华为电量文本解析（HFP 风格 AT 行 `+HUAWEIBATTERY=` / `+UPDATEHUAWEIBATTERY=`）。
//
// 移植自 Kotlin 权威源 HuaweiPods-main（FreeBuds for HyperOS / LSPosed 模块）
// `HuaweiBatteryParser.kt`，逐字段对齐；该源为经真机/模块验证的实现。
// 格式：首个数字为「对数 count」，其后 count 对 (key, value)。
// 键值：2=左耳电量 3=左耳充电 4=右耳电量 5=右耳充电 6=充电盒电量 7=充电盒充电。
//
// 注意（与权威源一致）：华为电量走 HFP/SPP 的 AT 文本行，PC 端 SPP 通道**可能无回包**；
// 本解析器仅负责“收到文本时正确解析”，是否在 PC 上真能拿到由设备/链路决定（待真机验证）。
internal sealed class HuaweiBatteryParser
{
    // 与 Kotlin 一致：允许 AT 前缀、UPDATE 前缀、= 或 : 分隔。
    private static readonly Regex BatteryPattern = new(
        @"(?:AT)?\+?(?:UPDATE)?HUAWEIBATTERY\s*[=:]\s*([0-9,\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record BatteryState(int? LeftPercent, int? RightPercent, int? CasePercent,
        bool LeftCharging, bool RightCharging, bool CaseCharging);

    private const int BatteryLeft = 2;
    private const int ChargingLeft = 3;
    private const int BatteryRight = 4;
    private const int ChargingRight = 5;
    private const int BatteryCase = 6;
    private const int ChargingCase = 7;

    public static BatteryState? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = BatteryPattern.Match(text);
        if (!match.Success)
            return null;
        var payload = match.Groups[1].Value;
        var numbers = new List<int>();
        foreach (var part in payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;
            if (int.TryParse(trimmed, out var value))
                numbers.Add(value);
        }
        if (numbers.Count < 2)
            return null;

        var pairValues = ExtractPairValues(numbers);
        if (pairValues.Count < 2)
            return null;

        var values = new Dictionary<int, int>();
        for (var i = 0; i + 1 < pairValues.Count; i += 2)
            values[pairValues[i]] = pairValues[i + 1];

        var left = Pod(values, BatteryLeft, ChargingLeft);
        var right = Pod(values, BatteryRight, ChargingRight);
        var @case = Pod(values, BatteryCase, ChargingCase);
        if (left.Percent is null && right.Percent is null && @case.Percent is null)
            return null;
        return new BatteryState(left.Percent, right.Percent, @case.Percent,
            left.Charging, right.Charging, @case.Charging);
    }

    // 首个数字为 count：当存在 count*2 个后续值，则截取其后 count*2 个作为键值对。
    private static List<int> ExtractPairValues(List<int> numbers)
    {
        var count = numbers[0];
        var expected = count * 2;
        if (count > 0 && numbers.Count >= expected + 1)
            return numbers.GetRange(1, expected);
        return numbers;
    }

    private static (int? Percent, bool Charging) Pod(Dictionary<int, int> values, int batteryKey, int chargingKey)
    {
        if (!values.TryGetValue(batteryKey, out var level) || level is < 0 or > 100)
            return (null, false);
        var charging = values.TryGetValue(chargingKey, out var c) && c != 0;
        return (level, charging);
    }
}
