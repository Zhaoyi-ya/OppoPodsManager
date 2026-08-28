using System.Text.Json;
using System.Text.Json.Serialization;

namespace OppoPodsManager.Assets.UserSettings;

public sealed class SettingsManager
{
    private readonly object _gate = new();
    private readonly string _path;
    private AppSettings _settings;

    public SettingsManager(string path)
    {
        _path = path;
        _settings = Load(path);
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppSettings changed;
        lock (_gate)
        {
            changed = update(_settings);
            if (changed == _settings)
                return;

            _settings = changed;
            Save(_path, changed);
        }

        Changed?.Invoke(this, changed);
    }

    // 保存开机启动设置并同步 Windows 登录启动项。
    public void SetAutoStart(bool enabled)
    {
        Update(settings => settings with { StartWithWindows = enabled });
        WindowsStartup.TrySetEnabled(enabled);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return AppSettings.Default;

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        catch (IOException)
        {
            return AppSettings.Default;
        }
    }

    private static void Save(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);

        File.Move(temporaryPath, path, true);
    }
}

public sealed record AppSettings(
    string Theme,
    string Language,
    bool MinimizeToTray,
    bool StartWithWindows,
    bool AutomaticUpdateChecks,
    int CardOpacity,
    int ToastDurationSeconds,
    Dictionary<string, string> ModelOverrides,
    Dictionary<string, string> DeviceNames,
    HashSet<string> HiddenMultiDeviceAddresses)
{
    // 保存个性化页的背景和窗口渲染选项。
    public string BackgroundPath { get; init; } = string.Empty;
    public int BackgroundBlur { get; init; }
    public bool AdvancedRender { get; init; }
    public bool AcrylicBlur { get; init; }
    public List<string> BackgroundHistory { get; init; } = [];
    public string SkippedVersion { get; init; } = string.Empty;

    public static AppSettings Default { get; } = new(
        "System",
        // 语言默认「自动」（空串 = 跟随系统语言，见 LanguageManager.AutomaticCultureCode）。
        // 之前误写成 "en"，导致首次启动强制英文、且「自动」选项形同虚设。
        "",
        true,
        false,
        true,
        50,
        5,
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
