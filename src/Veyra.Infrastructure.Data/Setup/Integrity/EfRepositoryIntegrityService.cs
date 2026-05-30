using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Integrity;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup.Models.Integrity;

namespace Veyra.Infrastructure.Data.Setup;

public sealed class EfRepositoryIntegrityService(
    VeyraDbContext db,
    IConfiguration configuration,
    IUserProfileRepository userProfiles,
    ICloudSyncService cloudSync,
    ILogger<EfRepositoryIntegrityService> log)
    : IRepositoryIntegrityService
{
    private const string ManagedHashPrefix = "sha256-";
    private static readonly ConcurrentDictionary<int, DateTime> LastRunByRepository = new();

    public async Task<IReadOnlyList<RepositoryIntegrityRunResultDto>> VerifyDueRepositoriesAsync(
        int intervalMinutes,
        bool repairFromCloud = false,
        int maxIssueSamples = 100,
        IProgress<RepositoryIntegrityProgressDto>? progress = null,
        CancellationToken ct = default)
    {
        log.LogInformation(
            "Due integrity verification started. IntervalMinutes {IntervalMinutes}. RepairFromCloud {RepairFromCloud}. MaxIssueSamples {MaxIssueSamples}",
            intervalMinutes,
            repairFromCloud,
            maxIssueSamples);
        var now = DateTime.UtcNow;
        var safeInterval = TimeSpan.FromMinutes(Math.Clamp(intervalMinutes, 5, 24 * 60));

        var repositoryIds = await db.Set<Repository>()
            .Where(r => !r.IsDeleted)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var due = new List<int>(repositoryIds.Count);
        foreach (var repositoryId in repositoryIds)
        {
            if (LastRunByRepository.TryGetValue(repositoryId, out var lastRun)
                && now - lastRun < safeInterval)
                continue;

            due.Add(repositoryId);
        }

        var staleIds = LastRunByRepository.Keys.Except(repositoryIds).ToList();
        foreach (var stale in staleIds)
            LastRunByRepository.TryRemove(stale, out _);

        var results = new List<RepositoryIntegrityRunResultDto>(due.Count);
        foreach (var repositoryId in due)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await VerifyRepositoryAsync(
                    repositoryId,
                    repairFromCloud,
                    maxIssueSamples,
                    progress,
                    ct);

                results.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled integrity verify failed for repository {RepositoryId}", repositoryId);
            }
            finally
            {
                LastRunByRepository[repositoryId] = DateTime.UtcNow;
            }
        }

        log.LogInformation(
            "Due integrity verification finished. DueRepositories {DueRepositories}. CompletedRuns {CompletedRuns}",
            due.Count,
            results.Count);

        return results;
    }

    public async Task<RepositoryIntegrityRunResultDto> VerifyRepositoryAsync(
        int repositoryId,
        bool repairFromCloud = false,
        int maxIssueSamples = 200,
        IProgress<RepositoryIntegrityProgressDto>? progress = null,
        CancellationToken ct = default)
    {
        log.LogInformation(
            "Repository integrity verification started. RepositoryId {RepositoryId}. RepairFromCloud {RepairFromCloud}. MaxIssueSamples {MaxIssueSamples}",
            repositoryId,
            repairFromCloud,
            maxIssueSamples);
        var startedAt = DateTime.UtcNow;
        var safeSamples = Math.Clamp(maxIssueSamples, 10, 5000);

        progress?.Report(new RepositoryIntegrityProgressDto("start", 0, "Preparing integrity verification..."));

        var repositoryExists = await db.Set<Repository>()
            .IgnoreQueryFilters()
            .AnyAsync(r => r.Id == repositoryId && !r.IsDeleted, ct);

        if (!repositoryExists)
        {
            return new RepositoryIntegrityRunResultDto(
                repositoryId,
                repairFromCloud,
                startedAt,
                DateTime.UtcNow,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                "Repository not found.");
        }

        progress?.Report(new RepositoryIntegrityProgressDto("collect", 10, "Collecting block references..."));

        var references = await db.Set<FileVersionBlock>()
            .Where(b => !b.IsDeleted)
            .Where(b => !b.FileVersion.IsDeleted)
            .Where(b => !b.FileVersion.FileIdentity.IsDeleted)
            .Where(b => b.FileVersion.FileIdentity.RepositoryId == repositoryId)
            .Select(b => new BlockReference(
                b.BlockStorageKey,
                b.FileVersionId,
                b.FileVersion.FileIdentity.RelativePath,
                b.Sequence,
                b.LengthBytes))
            .ToListAsync(ct);

        if (references.Count == 0)
        {
            return new RepositoryIntegrityRunResultDto(
                repositoryId,
                repairFromCloud,
                startedAt,
                DateTime.UtcNow,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                "No file-version blocks found for integrity verification.");
        }

        var refsByHash = references
            .GroupBy(r => r.BlockHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        string? accessToken = null;
        if (repairFromCloud)
        {
            var profile = await userProfiles.GetActiveProfileAsync(ct);
            if (!string.IsNullOrWhiteSpace(profile?.AccessToken))
                accessToken = profile.AccessToken;
        }

        progress?.Report(new RepositoryIntegrityProgressDto("verify", 20, "Verifying block artifacts..."));

        var outcomes = new Dictionary<string, VerificationOutcome>(StringComparer.OrdinalIgnoreCase);
        var verified = 0;
        var repaired = 0;
        var missing = 0;
        var corrupted = 0;

        var hashes = refsByHash.Keys.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < hashes.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var hash = hashes[index];
            var outcome = await VerifySingleHashAsync(hash, accessToken, repairFromCloud, ct);
            outcomes[hash] = outcome;

            switch (outcome.State)
            {
                case VerificationState.Verified:
                    verified++;
                    break;
                case VerificationState.Repaired:
                    repaired++;
                    break;
                case VerificationState.Missing:
                    missing++;
                    break;
                case VerificationState.Corrupted:
                    corrupted++;
                    break;
            }

            if ((index + 1) % 20 == 0 || index == hashes.Count - 1)
            {
                var percent = 20 + (int)Math.Round(((index + 1d) / hashes.Count) * 60d);
                progress?.Report(new RepositoryIntegrityProgressDto(
                    "verify",
                    Math.Clamp(percent, 20, 80),
                    $"Verified {index + 1}/{hashes.Count} blocks..."));
            }
        }

        progress?.Report(new RepositoryIntegrityProgressDto("report", 90, "Building integrity report..."));

        var issues = BuildIssueSamples(refsByHash, outcomes, safeSamples);
        var unresolved = missing + corrupted;

        var finishedAt = DateTime.UtcNow;
        var summary = $"Integrity check: refs={references.Count}, unique={hashes.Count}, verified={verified}, repaired={repaired}, missing={missing}, corrupted={corrupted}.";

        progress?.Report(new RepositoryIntegrityProgressDto("done", 100, "Integrity verification finished."));

        var result = new RepositoryIntegrityRunResultDto(
            repositoryId,
            repairFromCloud,
            startedAt,
            finishedAt,
            references.Count,
            hashes.Count,
            verified,
            missing,
            corrupted,
            repaired,
            unresolved,
            issues,
            summary);

        log.LogInformation(
            "Repository integrity verification finished. RepositoryId {RepositoryId}. UniqueBlocks {UniqueBlocks}. Verified {Verified}. Repaired {Repaired}. Missing {Missing}. Corrupted {Corrupted}",
            repositoryId,
            result.UniqueBlockCount,
            result.VerifiedBlockCount,
            result.RepairedBlockCount,
            result.MissingBlockCount,
            result.CorruptedBlockCount);

        return result;
    }

    private async Task<VerificationOutcome> VerifySingleHashAsync(
        string blockHash,
        string? accessToken,
        bool repairFromCloud,
        CancellationToken ct)
    {
        var path = ResolveLocalBlockPath(blockHash);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new VerificationOutcome(
                VerificationState.Missing,
                "Unable to resolve local block path.",
                path,
                Repaired: false);
        }

        if (!File.Exists(path))
        {
            if (repairFromCloud && !string.IsNullOrWhiteSpace(accessToken))
            {
                var restored = await TryRepairBlockFromCloudAsync(accessToken, blockHash, path, ct);
                if (restored)
                {
                    return new VerificationOutcome(
                        VerificationState.Repaired,
                        "Block was restored from cloud.",
                        path,
                        Repaired: true);
                }
            }

            return new VerificationOutcome(
                VerificationState.Missing,
                "Block file is missing.",
                path,
                Repaired: false);
        }

        if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var expected = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
            var actual = await ComputeFileSha256Async(path, ct);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                if (repairFromCloud && !string.IsNullOrWhiteSpace(accessToken))
                {
                    var restored = await TryRepairBlockFromCloudAsync(accessToken, blockHash, path, ct);
                    if (restored)
                    {
                        return new VerificationOutcome(
                            VerificationState.Repaired,
                            "Corrupted block was replaced from cloud.",
                            path,
                            Repaired: true);
                    }
                }

                return new VerificationOutcome(
                    VerificationState.Corrupted,
                    $"SHA-256 mismatch. Expected {expected}, actual {actual}.",
                    path,
                    Repaired: false);
            }
        }

        return new VerificationOutcome(
            VerificationState.Verified,
            "Block verified.",
            path,
            Repaired: false);
    }

    private async Task<bool> TryRepairBlockFromCloudAsync(
        string accessToken,
        string blockHash,
        string localPath,
        CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = localPath + "." + Guid.NewGuid().ToString("N") + ".repair";
            try
            {
                var restored = await cloudSync.DownloadBlockToFileAsync(accessToken, blockHash, tempPath, ct);
                if (!restored)
                    return false;

                var fileInfo = new FileInfo(tempPath);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                    return false;

                if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var expected = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
                    var actual = await ComputeFileSha256Async(tempPath, ct);
                    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                File.Move(tempPath, localPath, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best-effort cleanup of interrupted repair downloads.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Cloud repair failed for block {BlockHash}", blockHash);
            return false;
        }
    }

    private static List<RepositoryIntegrityIssueDto> BuildIssueSamples(
        IReadOnlyDictionary<string, List<BlockReference>> refsByHash,
        IReadOnlyDictionary<string, VerificationOutcome> outcomes,
        int maxIssueSamples)
    {
        var issues = new List<RepositoryIntegrityIssueDto>(Math.Min(maxIssueSamples, 512));

        foreach (var pair in refsByHash.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!outcomes.TryGetValue(pair.Key, out var outcome))
                continue;

            if (outcome.State is not (VerificationState.Missing or VerificationState.Corrupted))
                continue;

            var code = outcome.State == VerificationState.Missing
                ? "missing_block"
                : "corrupted_block";

            var severity = outcome.State == VerificationState.Missing
                ? "error"
                : "critical";

            foreach (var reference in pair.Value.OrderBy(r => r.FileVersionId).ThenBy(r => r.Sequence))
            {
                issues.Add(new RepositoryIntegrityIssueDto(
                    code,
                    severity,
                    reference.BlockHash,
                    reference.FileVersionId,
                    reference.RelativePath,
                    $"seq={reference.Sequence}, len={reference.LengthBytes}, {outcome.Message}"));

                if (issues.Count >= maxIssueSamples)
                    return issues;
            }
        }

        return issues;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0)
                break;

            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private string? ResolveLocalBlockPath(string blockHash)
    {
        var root = ResolveBlockStoreRoot();

        if (blockHash.StartsWith(ManagedHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = blockHash[ManagedHashPrefix.Length..].Trim().ToLowerInvariant();
            if (hash.Length < 4)
                return null;

            var p1 = hash[..2];
            var p2 = hash[2..4];
            return Path.Combine(root, "managed", "blocks", p1, p2, hash + ".bin");
        }

        var safe = blockHash.Trim().ToLowerInvariant()
            .Replace('/', '_')
            .Replace('\\', '_');

        if (safe.Length < 4)
            return null;

        var candidate1 = Path.Combine(root, "managed", "blocks", safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate1))
            return candidate1;

        var candidate2 = Path.Combine(root, safe[..2], safe[2..4], safe + ".bin");
        if (File.Exists(candidate2))
            return candidate2;

        return candidate1;
    }

    private string ResolveBlockStoreRoot()
    {
        var fromCfg = configuration["Storage:BlockStorePath"];
        var fromEnv = Environment.GetEnvironmentVariable("VEYRA_BLOCK_STORE");

        var root = !string.IsNullOrWhiteSpace(fromCfg)
            ? fromCfg.Trim()
            : !string.IsNullOrWhiteSpace(fromEnv)
                ? fromEnv.Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyraFlow",
                    "block-store");

        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

}
