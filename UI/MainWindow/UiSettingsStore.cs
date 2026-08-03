using NextSettingsManager = OppoPodsManager.Assets.UserSettings.SettingsManager;

namespace OppoPodsManager.UI.MainWindow;

// 统一访问旧项目和 Next 的界面设置，隔离设置格式与窗口交互。
internal sealed class UiSettingsStore
{
    private readonly NextSettingsManager? _nextSettings;

    public UiSettingsStore(NextSettingsManager? nextSettings)
    {
        _nextSettings = nextSettings;
    }

    // 读取布尔型界面设置。
    public bool GetBool(string key, bool fallback)
        => key switch
            {
                "AdvancedRender" => _nextSettings?.Current.AdvancedRender ?? fallback,
                "AcrylicBlur" => _nextSettings?.Current.AcrylicBlur ?? fallback,
                "TrayEnabled" => _nextSettings?.Current.MinimizeToTray ?? fallback,
                "AutoStart" => _nextSettings?.Current.StartWithWindows ?? fallback,
                "AutoCheckUpdate" => _nextSettings?.Current.AutomaticUpdateChecks ?? fallback,
                _ => fallback
            };

    // 读取整型界面设置。
    public int GetInt(string key, int fallback)
        => key switch
            {
                "CardOpacity" => _nextSettings?.Current.CardOpacity ?? fallback,
                "BgBlur" => _nextSettings?.Current.BackgroundBlur ?? fallback,
                "ToastDuration" => _nextSettings is null
                    ? fallback
                    : Math.Clamp(_nextSettings.Current.ToastDurationSeconds, 3, 8),
                _ => fallback
            };

    // 读取字符串型界面设置。
    public string? GetString(string key)
        => key switch
            {
                "BgCurrent" => _nextSettings?.Current.BackgroundPath,
                "Language" => _nextSettings?.Current.Language,
                "Theme" => _nextSettings?.Current.Theme,
                "CustomName" => _nextSettings?.Current.DeviceNames.TryGetValue("global", out var name) == true ? name : null,
                "ModelOverride" => _nextSettings?.Current.ModelOverrides.TryGetValue("global", out var model) == true ? model : null,
                _ => null
            };

    // 读取字符串列表设置。
    public IReadOnlyList<string> GetStringList(string key)
        => key == "BgHistory" ? _nextSettings?.Current.BackgroundHistory ?? [] : [];

    // 读取隐藏的多设备地址集合。
    public IReadOnlySet<string> GetHiddenMultiDevices()
        => _nextSettings?.Current.HiddenMultiDeviceAddresses
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 保存隐藏的多设备地址集合。
    public void SetHiddenMultiDevices(IEnumerable<string> addresses)
    {
        var normalized = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _nextSettings?.Update(settings => settings with { HiddenMultiDeviceAddresses = normalized });
    }

    // 保存布尔型界面设置。
    public void SetBool(string key, bool value)
    {
        if (key == "AutoStart")
        {
            _nextSettings?.SetAutoStart(value);
            return;
        }

        _nextSettings?.Update(settings => key switch
        {
            "AdvancedRender" => settings with { AdvancedRender = value },
            "AcrylicBlur" => settings with { AcrylicBlur = value },
            "TrayEnabled" => settings with { MinimizeToTray = value },
            "AutoCheckUpdate" => settings with { AutomaticUpdateChecks = value },
            _ => settings
        });
    }

    // 保存整型界面设置。
    public void SetInt(string key, int value)
    {
        _nextSettings?.Update(settings => key switch
        {
            "CardOpacity" => settings with { CardOpacity = value },
            "BgBlur" => settings with { BackgroundBlur = value },
            "ToastDuration" => settings with { ToastDurationSeconds = Math.Clamp(value, 3, 8) },
            _ => settings
        });
    }

    // 保存字符串型界面设置。
    public void SetString(string key, string? value)
    {
        _nextSettings?.Update(settings => key switch
        {
            "BgCurrent" => settings with { BackgroundPath = value ?? string.Empty },
            "Language" => settings with { Language = value ?? "zh-Hans" },
            "Theme" => settings with { Theme = value ?? "System" },
            "CustomName" => settings with { DeviceNames = UpdateSettingMap(settings.DeviceNames, "global", value) },
            "ModelOverride" => settings with { ModelOverrides = UpdateSettingMap(settings.ModelOverrides, "global", value) },
            _ => settings
        });
    }

    // 保存字符串列表设置。
    public void SetStringList(string key, IReadOnlyList<string> values)
    {
        if (key == "BgHistory")
            _nextSettings?.Update(settings => settings with { BackgroundHistory = values.ToList() });
    }

    // 更新 Next 设置中的按设备映射值。
    private static Dictionary<string, string> UpdateSettingMap(
        IReadOnlyDictionary<string, string> source,
        string key,
        string? value)
    {
        var copy = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            copy.Remove(key);
        else
            copy[key] = value;
        return copy;
    }
}
