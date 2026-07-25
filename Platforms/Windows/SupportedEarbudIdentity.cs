using System.Text;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Lightweight candidate gate for discovery/watchers.
/// Full product confirmation still happens via brand handshake.
/// </summary>
public static class SupportedEarbudIdentity
{
    private static readonly string[] NameHints =
    [
        "enco", "buds", "pod", "freebuds", "ear", "wh-", "wf-", "linkbuds",
        "oneplus buds", "realme buds", "oppo enco", "nothing ear",
    ];

    public static bool IsSupportedName(string? deviceName)
    {
        var normalized = Normalize(deviceName);
        if (normalized.Length == 0) return false;
        return NameHints.Any(hint => normalized.Contains(hint, StringComparison.Ordinal));
    }

    public static bool IsCandidate(string? deviceName, bool hasMelodyService) =>
        hasMelodyService || IsSupportedName(deviceName);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is ' ' or '-')
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
