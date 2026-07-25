using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Irihi.Lingua;

namespace OppoPodsManager.Localization;

public sealed record LanguageOption(string CultureCode, string DisplayName)
{
    public bool IsAutomatic => string.IsNullOrEmpty(CultureCode);

    public override string ToString() => DisplayName;
}

[LinguaManager("./Localization/Strings.json")]
public partial class LanguageManager
{
    public const string AutomaticCultureCode = "";
    private const string DefaultCultureCode = "zh-Hans";

    public static IReadOnlyList<LanguageOption> GetAvailableLanguages()
    {
        var languages = new List<LanguageOption>
        {
            new(AutomaticCultureCode, ResolveString(Instance.Personal_LanguageAuto)),
            new(DefaultCultureCode, CultureInfo.GetCultureInfo(DefaultCultureCode).NativeName)
        };

        var assembly = typeof(LanguageManager).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(".Strings.json", StringComparison.OrdinalIgnoreCase)
                         || name.Contains(".Strings.", StringComparison.OrdinalIgnoreCase)))
        {
            var markerIndex = resourceName.LastIndexOf(".Strings.", StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;

            var cultureName = resourceName[(markerIndex + ".Strings.".Length)..];
            if (cultureName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                cultureName = cultureName[..^5];

            if (string.Equals(cultureName, "json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(cultureName) || !TryCreateCulture(cultureName, out var culture))
                continue;

            if (string.Equals(cultureName, DefaultCultureCode, StringComparison.OrdinalIgnoreCase)
                || languages.Any(x => string.Equals(x.CultureCode, cultureName, StringComparison.OrdinalIgnoreCase)))
                continue;

            languages.Add(new LanguageOption(cultureName, culture.NativeName));
        }

        return languages;
    }

    public static CultureInfo ResolveCulture(string? configuredCulture)
    {
        var languages = GetAvailableLanguages();
        if (!string.IsNullOrWhiteSpace(configuredCulture)
            && languages.Any(x => string.Equals(x.CultureCode, configuredCulture, StringComparison.OrdinalIgnoreCase))
            && TryCreateCulture(configuredCulture, out var configured))
        {
            return configured;
        }

        var systemCulture = CultureInfo.CurrentUICulture;
        var match = languages.FirstOrDefault(x => string.Equals(x.CultureCode, systemCulture.Name, StringComparison.OrdinalIgnoreCase));
        if (match != null && TryCreateCulture(match.CultureCode, out var exact))
            return exact;

        var parent = systemCulture.Parent;
        while (!string.IsNullOrEmpty(parent.Name))
        {
            match = languages.FirstOrDefault(x => string.Equals(x.CultureCode, parent.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null && TryCreateCulture(match.CultureCode, out var parentCulture))
                return parentCulture;
            parent = parent.Parent;
        }

        return CultureInfo.InvariantCulture;
    }

    public static void ApplyConfiguredCulture(string? configuredCulture)
    {
        Instance.UpdateCulture(ResolveCulture(configuredCulture));
    }

    private static bool TryCreateCulture(string cultureName, out CultureInfo culture)
    {
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName);
            return true;
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
            return false;
        }
    }

    private static string ResolveString(IObservable<string?> observable)
    {
        string? value = null;
        using var subscription = observable.Subscribe(v => value = v);
        return value ?? string.Empty;
    }

    /// <summary>
    /// Gets the current value of an observable resource synchronously.
    /// The observable emits its current value immediately on subscribe (behavior-subject semantics).
    /// </summary>
    public string GetString(IObservable<string?> observable)
    {
        string? value = null;
        using var sub = observable.Subscribe(v => value = v);
        return value ?? "";
    }
}
