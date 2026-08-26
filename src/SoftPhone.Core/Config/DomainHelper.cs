using System.Text.RegularExpressions;

namespace SoftPhone.Core.Config;

/// <summary>
/// Pure domain helpers (no Windows/WebView2 APIs) — safe to import in unit tests.
/// Mirrors the extension's src/common/domain.ts.
/// </summary>
public static partial class DomainHelper
{
    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"/.*$")]
    private static partial Regex PathRegex();

    /// <summary>Normalize a user-entered domain to a bare host (strip scheme, path, trailing slash).</summary>
    public static string NormalizeDomain(string input)
    {
        var s = (input ?? "").Trim();
        s = SchemeRegex().Replace(s, "");
        s = PathRegex().Replace(s, "");
        return s.ToLowerInvariant();
    }

    /// <summary>Origin (https) for a bare domain.</summary>
    public static string OriginFor(string domain) => $"https://{NormalizeDomain(domain)}";

    /// <summary>Is this a plausible, non-empty host we can build a URL from?</summary>
    public static bool IsValidDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var host = NormalizeDomain(domain);
        // At least one dot, no spaces, and a valid absolute https URL.
        return host.Length > 0
            && host.Contains('.')
            && !host.Contains(' ')
            && Uri.TryCreate($"https://{host}", UriKind.Absolute, out _);
    }
}
