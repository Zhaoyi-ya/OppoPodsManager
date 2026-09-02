namespace OppoPodsManager.Assets.UserSettings;

// 为界面和托盘提供统一的设置访问，集中处理应用设置字段与存储模型的映射。
public sealed class SettingsStore
{
    private readonly SettingsManager? _settings;

    public SettingsStore(SettingsManager? settings)
    {
        _settings = settings;
    }

    // 读取布尔设置。
    public bool GetBool(string key, bool fallback)
        => key switch
        {
            "AdvancedRender" => _settings?.Current.AdvancedRender ?? fallback,
            "AcrylicBlur" => _settings?.Current.AcrylicBlur ?? fallback,
            "TrayEnabled" => _settings?.Current.MinimizeToTray ?? fallback,
            "AutoStart" => _settings?.Current.StartWithWindows ?? fallback,
            "AutoCheckUpdate" => _settings?.Current.AutomaticUpdateChecks ?? fallback,
            _ => fallback
        };

    // 读取整数设置并限制界面允许的弹窗时长范围。
    public int GetInt(string key, int fallback)
        => key switch
        {
            "CardOpacity" => _settings?.Current.CardOpacity ?? fallback,
            "BgBlur" => _settings?.Current.BackgroundBlur ?? fallback,
            "ToastDuration" => _settings is null
                ? fallback
                : Math.Clamp(_settings.Current.ToastDurationSeconds, 3, 8),
            _ => fallback
        };

    // 读取字符串设置和按设备保存的单项设置。
    public string? GetString(string key)
        => key switch
        {
            "BgCurrent" => _settings?.Current.BackgroundPath,
            "Language" => _settings?.Current.Language,
            "Theme" => _settings?.Current.Theme,
            "CustomName" => _settings?.Current.DeviceNames.TryGetValue("global", out var name) == true ? name : null,
            "ModelOverride" => _settings?.Current.ModelOverrides.TryGetValue("global", out var model) == true ? model : null,
            _ => null
        };

    // 读取背景历史列表。
    public IReadOnlyList<string> GetStringList(string key)
        => key == "BgHistory" ? _settings?.Current.BackgroundHistory ?? [] : [];

    // 读取用户隐藏的多设备地址集合。
    public IReadOnlySet<string> GetHiddenMultiDevices()
        => _settings?.Current.HiddenMultiDeviceAddresses
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 按 MAC 地址查询已缓存的品牌（命中则优先尝试，减少识别等待）。MAC 格式不统一，
    // 此处归一化后再查，调用方无需关心分隔符/大小写。
    public string? GetBrandForMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return null;
        var key = NormalizeMac(mac);
        var cache = _settings?.Current.BrandByMac;
        return cache is not null && cache.TryGetValue(key, out var brand) ? brand : null;
    }

    // 设备协议确认成功后记录其 MAC → 品牌映射，供后续同设备优先命中。
    public void RecordBrandForMac(string? mac, string? brand)
    {
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(brand))
            return;
        var key = NormalizeMac(mac);
        _settings?.Update(settings =>
        {
            var current = settings.BrandByMac ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (current.TryGetValue(key, out var existing)
                && string.Equals(existing, brand, StringComparison.OrdinalIgnoreCase))
                return settings;
            var copy = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            copy[key] = brand!;
            return settings with { BrandByMac = copy };
        });
    }

    // 把 "AA:BB:CC:DD:EE:FF" / "aa-bb-..." 等统一成无分隔符大写形式，作为缓存键。
    private static string NormalizeMac(string mac)
        => new string(mac.Where(ch => ch != ':' && ch != '-').ToArray()).ToUpperInvariant();

    // 保存用户隐藏的多设备地址集合。
    public void SetHiddenMultiDevices(IEnumerable<string> addresses)
    {
        var normalized = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings?.Update(settings => settings with { HiddenMultiDeviceAddresses = normalized });
    }

    // 保存布尔设置，并通过设置管理器同步开机自启项。
    public void SetBool(string key, bool value)
    {
        if (key == "AutoStart")
        {
            _settings?.SetAutoStart(value);
            return;
        }

        _settings?.Update(settings => key switch
        {
            "AdvancedRender" => settings with { AdvancedRender = value },
            "AcrylicBlur" => settings with { AcrylicBlur = value },
            "TrayEnabled" => settings with { MinimizeToTray = value },
            "AutoCheckUpdate" => settings with { AutomaticUpdateChecks = value },
            _ => settings
        });
    }

    // 保存整数设置。
    public void SetInt(string key, int value)
        => _settings?.Update(settings => key switch
        {
            "CardOpacity" => settings with { CardOpacity = value },
            "BgBlur" => settings with { BackgroundBlur = value },
            "ToastDuration" => settings with { ToastDurationSeconds = Math.Clamp(value, 3, 8) },
            _ => settings
        });

    // 保存字符串设置和按设备保存的单项设置。
    public void SetString(string key, string? value)
        => _settings?.Update(settings => key switch
        {
            "BgCurrent" => settings with { BackgroundPath = value ?? string.Empty },
            "Language" => settings with { Language = value ?? "zh-Hans" },
            "Theme" => settings with { Theme = value ?? "System" },
            "CustomName" => settings with { DeviceNames = UpdateSettingMap(settings.DeviceNames, "global", value) },
            "ModelOverride" => settings with { ModelOverrides = UpdateSettingMap(settings.ModelOverrides, "global", value) },
            _ => settings
        });

    // 保存背景历史列表。
    public void SetStringList(string key, IReadOnlyList<string> values)
    {
        if (key == "BgHistory")
            _settings?.Update(settings => settings with { BackgroundHistory = values.ToList() });
    }

    // 更新设置中按键保存的字符串值。
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
