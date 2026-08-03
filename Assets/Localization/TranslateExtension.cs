using Avalonia;
using Avalonia.Markup.Xaml;
using System.Reflection;
using System.Text.Json;

namespace OppoPodsManager.Assets.Localization;

// 为 AXAML 提供基于资源键的本地化标记扩展。
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension()
    {
    }

    public TranslateExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    // 将 AXAML 内的资源键解析为当前语言文案，并登记目标属性以便语言切换时刷新。
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var value = TranslationCatalog.Get(Key);
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target
            && target.TargetObject is AvaloniaObject targetObject
            && target.TargetProperty is AvaloniaProperty targetProperty)
        {
            TranslationCatalog.Register(targetObject, targetProperty, Key);
        }

        return value;
    }
}

internal static class TranslationCatalog
{
    private static readonly object RegistrationGate = new();
    private static readonly List<TranslationTarget> Targets = new();

    // 缓存默认语言的文案，供 AXAML 在加载时同步查询。
    private static IReadOnlyDictionary<string, string> _values = LoadValues("zh");
    private static string _language = "zh";

    // 通知响应式文案对象和界面资源目标刷新当前语言。
    public static event Action? LanguageChanged;

    public static string CurrentLanguage => Volatile.Read(ref _language);

    // 登记 AXAML 本地化目标，弱引用保证窗口销毁后不会被语言目录保留。
    public static void Register(AvaloniaObject targetObject, AvaloniaProperty targetProperty, string key)
    {
        lock (RegistrationGate)
        {
            Targets.RemoveAll(target => !target.Object.TryGetTarget(out _));
            Targets.Add(new TranslationTarget(targetObject, targetProperty, key));
        }
    }

    // 找不到文案时回退显示资源键，保证窗口仍能加载。
    public static string Get(string key)
    {
        var values = Volatile.Read(ref _values);
        return values.TryGetValue(key, out var value) ? value : key.Replace('_', ' ');
    }

    // 在启动阶段选择嵌入语言包，并把系统文化码转换成资源文件使用的短代码。
    public static void SetLanguage(string language)
    {
        var catalogLanguage = NormalizeLanguage(language);
        Volatile.Write(ref _language, catalogLanguage);
        Volatile.Write(ref _values, LoadValues(catalogLanguage));
        RefreshTargets();
        LanguageChanged?.Invoke();
    }

    // 在 UI 线程重新写入所有已登记的 AXAML 属性，使语言切换立即反映到界面。
    private static void RefreshTargets()
    {
        TranslationTarget[] targets;
        lock (RegistrationGate)
        {
            targets = Targets.ToArray();
            Targets.RemoveAll(target => !target.Object.TryGetTarget(out _));
        }

        foreach (var target in targets)
        {
            if (!target.Object.TryGetTarget(out var targetObject))
                continue;

            try
            {
                targetObject.SetValue(target.Property, Get(target.Key));
            }
            catch (InvalidOperationException)
            {
                // 目标控件已进入模板销毁流程时，忽略这一次刷新。
            }
        }
    }

    private sealed class TranslationTarget
    {
        public TranslationTarget(AvaloniaObject targetObject, AvaloniaProperty property, string key)
        {
            Object = new WeakReference<AvaloniaObject>(targetObject);
            Property = property;
            Key = key;
        }

        public WeakReference<AvaloniaObject> Object { get; }
        public AvaloniaProperty Property { get; }
        public string Key { get; }
    }

    // 将设置文件中的文化码统一映射到实际嵌入资源名称。
    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "zh";

        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        if (language.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            return "de";
        if (language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            return "ru";
        return "zh";
    }

    private static IReadOnlyDictionary<string, string> LoadValues(string language)
    {
        // 从嵌入资源读取字典，保证 AOT 发布后不依赖外部文件路径。
        var resourceName = language switch
        {
            "en" => "OppoPodsManager.Assets.Localization.Strings.en.json",
            "de" => "OppoPodsManager.Assets.Localization.Strings.de.json",
            "ru" => "OppoPodsManager.Assets.Localization.Strings.ru.json",
            _ => "OppoPodsManager.Assets.Localization.Strings.json"
        };
        using var stream = typeof(TranslationCatalog).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new Dictionary<string, string>();

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }
}
