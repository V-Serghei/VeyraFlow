using Microsoft.Extensions.Configuration;

namespace Veyra.Infrastructure.Data.Setup;

internal static class RepositoryBundleBlockPathResolver
{
    private const string ManagedHashPrefix = "sha256-";
    private const string LegacyManagedHashPrefix = "msha256:";

    public static string ResolveStoreRoot(IConfiguration configuration)
    {
        var fromConfig = configuration["Storage:BlockStorePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

        var root = !string.IsNullOrWhiteSpace(fromConfig)
            ? fromConfig.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "block-store");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    public static string? ResolveLocalBlockPath(string storeRoot, string blockHash)
    {
        if (string.IsNullOrWhiteSpace(blockHash))
            return null;

        var normalized = blockHash.Trim();

        if (normalized.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(LegacyManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = normalized.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase)
                ? normalized[ManagedHashPrefix.Length..]
                : normalized[LegacyManagedHashPrefix.Length..];

            hash = hash.Trim().ToLowerInvariant();
            if (hash.Length < 4)
                return null;

            return Path.Combine(storeRoot, "managed", "blocks", hash[..2], hash[2..4], hash + ".bin");
        }

        var safe = normalized.ToLowerInvariant()
            .Replace('/', '_')
            .Replace('\\', '_');

        if (safe.Length < 4)
            return null;

        var nativeCandidate = Path.Combine(storeRoot, "blocks", safe[..2], safe[2..4], safe + ".zst");
        if (File.Exists(nativeCandidate))
            return nativeCandidate;

        var managedCandidate = Path.Combine(storeRoot, "managed", "blocks", safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(managedCandidate))
            return managedCandidate;

        var legacyCandidate = Path.Combine(storeRoot, safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(legacyCandidate))
            return legacyCandidate;

        return nativeCandidate;
    }
}
