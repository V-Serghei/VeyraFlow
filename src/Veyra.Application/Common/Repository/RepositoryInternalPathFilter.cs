namespace Veyra.Application.Common.Repository;

public static class RepositoryInternalPathFilter
{
    private static readonly string[] InternalDirectoryNames =
    [
        ".veyra-restores",
        ".veyra-rollback-backups"
    ];

    public static bool ShouldIgnoreForSnapshotRestore(string? relativePath)
    {
        var normalized = Normalize(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (ContainsInternalDirectory(normalized))
            return true;

        return IsOfficeLockFile(normalized);
    }

    public static bool IsInternalVeyraPath(string? relativePath)
    {
        var normalized = Normalize(relativePath);
        return !string.IsNullOrWhiteSpace(normalized) && ContainsInternalDirectory(normalized);
    }

    private static bool ContainsInternalDirectory(string normalizedPath)
    {
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment => InternalDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsOfficeLockFile(string normalizedPath)
    {
        var fileName = Path.GetFileName(normalizedPath);
        return fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('\\', '/').Trim('/');
}
