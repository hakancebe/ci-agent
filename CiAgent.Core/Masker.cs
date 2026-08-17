using System.Text.RegularExpressions;

namespace CiAgent.Core;

public static class Masker
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}"), "***GITHUB_TOKEN***"),
        (new Regex(@"github_pat_[A-Za-z0-9_]{20,}"), "***GITHUB_PAT***"),
        (new Regex(@"AKIA[0-9A-Z]{16}"), "***AWS_KEY***"),
        (new Regex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]{20,}=*"), "Bearer ***"),
        (new Regex(@"(?i)\b(password|pwd|secret|api[-_]?key|token)\b\s*[:=]\s*(?!\*\*\*)\S+"), "$1=***"),
        (new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), "***EMAIL***"),
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