using System.Text.RegularExpressions;

namespace Veyra.Infrastructure.Native.Scanning;

internal static class RepositoryScanExclusionMatcher
{
    public static bool IsExcluded(string? relativePath, IReadOnlyCollection<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || patterns.Count == 0)
            return false;

        var normalizedPath = NormalizePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return false;

        var fileName = Path.GetFileName(normalizedPath);

        foreach (var rawPattern in patterns)
        {
            var pattern = NormalizePattern(rawPattern);
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (WildcardMatch(normalizedPath, pattern) || WildcardMatch(fileName, pattern))
                    return true;

                continue;
            }

            if (normalizedPath.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string value)
        => value.Trim().Replace('\\', '/').Trim('/');

    private static string NormalizePattern(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('\\', '/').Trim('/');

    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
