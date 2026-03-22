using Microsoft.Extensions.Configuration;

namespace Veyra.Infrastructure.Data.Setup;

internal static class RepositorySnapshotArchivePathResolver
{
    public static string ResolveArchiveRoot(IConfiguration configuration)
    {
        var fromConfig = configuration["Storage:SnapshotArchivePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_SNAPSHOT_ARCHIVE");

        var root = !string.IsNullOrWhiteSpace(fromConfig)
            ? fromConfig.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "snapshot-archive");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    public static string BuildArchiveRelativePath(int repositoryId, long snapshotId, DateTime createdAtUtc, string? title)
    {
        var stamp = createdAtUtc.ToUniversalTime().ToString("yyyyMMdd_HHmmss");
        var safeTitle = SanitizeSegment(title);
        return Path.Combine(
            $"repo-{repositoryId}",
            createdAtUtc.ToUniversalTime().ToString("yyyy"),
            createdAtUtc.ToUniversalTime().ToString("MM"),
            $"{stamp}_{snapshotId}_{safeTitle}.zip");
    }

    public static string ResolveArchivePath(string archiveRoot, string relativePath)
        => Path.GetFullPath(Path.Combine(archiveRoot, relativePath));

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "snapshot";

        var cleaned = new string(value
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim('_', ' ');

        return string.IsNullOrWhiteSpace(cleaned) ? "snapshot" : cleaned;
    }
}
