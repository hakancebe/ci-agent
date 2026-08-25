using System.Text.RegularExpressions;

namespace CiAgent.Core;

public static class Masker
{
    // Tüm kurallara matchTimeout: devasa/patolojik log satırlarında katastrofik
    // backtracking'e karşı sigorta.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.None, MatchTimeout), "***GITHUB_TOKEN***"),
        (new Regex(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.None, MatchTimeout), "***GITHUB_PAT***"),
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.None, MatchTimeout), "***AWS_KEY***"),
        (new Regex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]{20,}=*", RegexOptions.None, MatchTimeout), "Bearer ***"),
        (new Regex(@"(?i)\b(password|pwd|secret|api[-_]?key|token)\b\s*[:=]\s*(?!\*\*\*)\S+", RegexOptions.None, MatchTimeout), "$1=***"),
        (new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.None, MatchTimeout), "***EMAIL***"),
    ];

    public static string Mask(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var result = input;
        foreach (var (pattern, replacement) in Rules)
            result = pattern.Replace(result, replacement);

        return result;
    }
}