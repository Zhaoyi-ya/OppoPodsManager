namespace OppoPodsManager;

/// <summary>ListBox 展示用数据项，区分内置/设备端/自定义预设。</summary>
public sealed class EqPresetItem
{
    public string Name { get; set; } = "";
    public bool IsCustom { get; set; }
    /// <summary>设备端预设的 eqId，仅 IsDeviceEntry 时有效。</summary>
    public int EqId { get; set; }
    public bool IsDeviceEntry => EqId != 0;
    public bool ShowDelete => IsCustom || IsDeviceEntry;
}
