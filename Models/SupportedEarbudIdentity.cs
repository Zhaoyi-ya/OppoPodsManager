using System;
using System.Linq;
using System.Text;

namespace OppoPodsManager;

/// <summary>
/// Determines whether a Bluetooth display name belongs to a known earbud model.
/// Brand names alone are deliberately not sufficient: phones such as OnePlus 12RT
/// must not become connection candidates.
/// </summary>
public static class SupportedEarbudIdentity
{
    private static readonly string[] KnownModelNames = DeviceProfileLoader.GetModelNames()
        .Select(Normalize)
        .Where(model => model.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static bool IsSupportedName(string? deviceName)
    {
        var normalizedName = Normalize(deviceName);
        if (normalizedName.Length == 0) return false;

        return KnownModelNames.Any(model =>
            normalizedName == model || normalizedName.StartsWith(model, StringComparison.Ordinal));
    }

    public static bool IsCandidate(string? deviceName, bool hasMelodyService) =>
        hasMelodyService || IsSupportedName(deviceName);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
