using MediatR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Common.Files;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed class UpdateRepositoryConfigurationHandler(
    IRepositoryRepository repositories,
    ISetupRepository setup,
    IRepositoryScanner scanner,
    INativeSetupApplier native,
    ILogger<UpdateRepositoryConfigurationHandler> log)
    : IRequestHandler<UpdateRepositoryConfigurationCommand, OperationResult>
{
    public async Task<OperationResult> Handle(UpdateRepositoryConfigurationCommand request, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();
        try
        {
            log.LogInformation("Updating repository configuration {RepositoryId}", request.RepositoryId);

            var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
            if (repo is null || repo.IsDeleted)
                return OperationResult.Fail("Repository was not found.");

            var normalizedPath = NormalizeDirectoryPath(request.DirectoryPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
                return OperationResult.Fail("Directory path does not exist.");

            var normalizedFormats = NormalizeFormats(request.Formats);
            if (normalizedFormats.Count == 0)
                return OperationResult.Fail("At least one format is required.");
            var normalizedExcludedPatterns = NormalizeExcludedPatterns(request.ExcludedPatterns);

            var safeName = string.IsNullOrWhiteSpace(request.Name)
                ? repo.Name
                : request.Name.Trim();

            var safeRetentionPolicy = NormalizeRetentionPolicy(request.RetentionPolicy);
            if (safeRetentionPolicy.HasLocalOverride
                && safeRetentionPolicy.Enabled
                && TargetsManualSnapshots(safeRetentionPolicy.TriggerFilters)
                && !safeRetentionPolicy.AllowManualSnapshotCleanup)
            {
                return OperationResult.Fail("Manual snapshot cleanup requires an explicit unlock.");
            }

            var safeSyncConflictStrategy = RepositorySyncConflictStrategies.Normalize(request.SyncConflictStrategy);
            var safeSyncRetryMaxAttempts = Math.Clamp(request.SyncRetryMaxAttempts, 1, 20);
            var safeSyncRetryBaseDelaySeconds = Math.Clamp(request.SyncRetryBaseDelaySeconds, 5, 600);
            var validationMs = stageTimer.ElapsedMilliseconds;

            var pathChanged = !PathEquals(repo.DirectoryPath, normalizedPath);
            var formatsChanged = !SetEquals(normalizedFormats, NormalizeFormats(repo.LinkedFormats));
            var excludedPatternsChanged = !SetEquals(normalizedExcludedPatterns, NormalizeExcludedPatterns(repo.ExcludedPatterns));
            var repositoryFieldsChanged =
                !string.Equals(repo.Name, safeName, StringComparison.Ordinal)
                || !string.Equals(repo.Description ?? string.Empty, request.Description ?? string.Empty, StringComparison.Ordinal)
                || repo.AutoCaptureFileVersions != request.AutoCaptureFileVersions
                || repo.ProtectCloudMetadata != request.ProtectCloudMetadata
                || excludedPatternsChanged
                || !RetentionPolicyEquals(NormalizeRetentionPolicy(repo.RetentionPolicy), safeRetentionPolicy)
                || !string.Equals(
                    RepositorySyncConflictStrategies.Normalize(repo.CloudSync?.ConflictStrategy),
                    safeSyncConflictStrategy,
                    StringComparison.OrdinalIgnoreCase)
                || (repo.CloudSync?.RetryMaxAttempts ?? 5) != safeSyncRetryMaxAttempts
                || (repo.CloudSync?.RetryBaseDelaySeconds ?? 30) != safeSyncRetryBaseDelaySeconds;

            if (!pathChanged && !formatsChanged && !repositoryFieldsChanged)
            {
                log.LogInformation(
                    "Repository configuration update skipped: no changes detected. RepositoryId {RepositoryId}. ValidationMs {ValidationMs}. TotalMs {TotalMs}",
                    request.RepositoryId,
                    validationMs,
                    totalTimer.ElapsedMilliseconds);
                return OperationResult.Ok();
            }

            if (!PathEquals(repo.DirectoryPath, normalizedPath))
                await setup.UpdateWatchedDirectoryAsync(repo.DirectoryPath, normalizedPath, ct);

            var directoryMs = stageTimer.ElapsedMilliseconds - validationMs;
            var dbSaveMs = 0L;
            if (pathChanged || repositoryFieldsChanged)
            {
                stageTimer.Restart();
                await repositories.UpdateRepositoryAsync(
                    repo.Id,
                    safeName,
                    request.Description,
                    request.AutoCaptureFileVersions,
                    request.ProtectCloudMetadata,
                    normalizedExcludedPatterns,
                    safeRetentionPolicy,
                    safeSyncConflictStrategy,
                    safeSyncRetryMaxAttempts,
                    safeSyncRetryBaseDelaySeconds,
                    ct);
                dbSaveMs = stageTimer.ElapsedMilliseconds;
            }

            var formatMs = 0L;
            if (pathChanged || formatsChanged)
            {
                stageTimer.Restart();
                var globalFormats = await setup.GetTrackedExtensionsAsync(ct);
                var missingGlobal = normalizedFormats
                    .Where(f => !globalFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var ext in missingGlobal)
                    await setup.AddTrackedExtensionAsync(ext, ct);

                var linkedFormats = await setup.GetFormatsForDirectoryAsync(normalizedPath, ct);

                var toLink = normalizedFormats
                    .Where(f => !linkedFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                var toUnlink = linkedFormats
                    .Where(f => !normalizedFormats.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (toUnlink.Count > 0)
                    await setup.UnlinkDirectoryFromFormatsAsync(normalizedPath, toUnlink, ct);

                if (toLink.Count > 0)
                    await setup.LinkDirectoryToFormatsAsync(normalizedPath, toLink, ct);
                formatMs = stageTimer.ElapsedMilliseconds;
            }

            var scanMs = 0L;
            var nativeApplyMs = 0L;
            var indexingSetupChanged = pathChanged || formatsChanged || excludedPatternsChanged;
            if (indexingSetupChanged)
            {
                stageTimer.Restart();
                await scanner.ScanRepositoryAsync(
                    repo.Id,
                    null,
                    new RepositoryScanOptionsDto(
                        SaveFileVersions: false,
                        TriggerOverride: "sync_index_config_update"),
                    ct);
                scanMs = stageTimer.ElapsedMilliseconds;

                stageTimer.Restart();
                await native.ApplySetupAsync(ct);
                nativeApplyMs = stageTimer.ElapsedMilliseconds;
            }

            log.LogInformation(
                "Repository {RepositoryId} updated. Path {Path}. Formats {FormatCount}. RetentionEnabled {RetentionEnabled}. SyncStrategy {SyncStrategy}. Changes Path {PathChanged}. Formats {FormatsChanged}. RepositoryFields {RepositoryFieldsChanged}. IndexingSetup {IndexingSetupChanged}. ValidationMs {ValidationMs}. DirectoryMs {DirectoryMs}. DbSaveMs {DbSaveMs}. FormatMs {FormatMs}. ScanMs {ScanMs}. NativeApplyMs {NativeApplyMs}. TotalMs {TotalMs}",
                repo.Id,
                normalizedPath,
                normalizedFormats.Count,
                safeRetentionPolicy.Enabled,
                safeSyncConflictStrategy,
                pathChanged,
                formatsChanged,
                repositoryFieldsChanged,
                indexingSetupChanged,
                validationMs,
                directoryMs,
                dbSaveMs,
                formatMs,
                scanMs,
                nativeApplyMs,
                totalTimer.ElapsedMilliseconds);

            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to update repository configuration {RepositoryId}", request.RepositoryId);
            return OperationResult.Fail(ex.Message);
        }
    }

    private static string NormalizeDirectoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFullPath(value.Trim().Replace('/', '\\')).TrimEnd('\\', '/');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
        => left.TrimEnd('\\', '/').Equals(right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool SetEquals(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
        => left.Count == right.Count && left.All(value => right.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static List<string> NormalizeFormats(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(KnownFileExtensions.NormalizeExtension)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RepositoryRetentionPolicyDto NormalizeRetentionPolicy(RepositoryRetentionPolicyDto policy)
    {
        var filters = policy.TriggerFilters
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return policy with
        {
            MaxAgeDays = NormalizePositive(policy.MaxAgeDays),
            MaxSnapshots = NormalizePositive(policy.MaxSnapshots),
            MaxTotalSizeBytes = NormalizePositive(policy.MaxTotalSizeBytes),
            TriggerFilters = filters,
            RunIntervalMinutes = Math.Clamp(policy.RunIntervalMinutes, 5, 7 * 24 * 60),
            StorageMode = RepositoryRetentionStorageModes.Normalize(policy.StorageMode),
            PolicySource = RepositoryRetentionPolicySources.Normalize(policy.PolicySource),
            AutomaticCompactionWindowHours = policy.AutomaticCompactionEnabled
                ? NormalizePositive(policy.AutomaticCompactionWindowHours)
                : null
        };
    }

    private static bool TargetsManualSnapshots(IReadOnlyCollection<string> triggerFilters)
    {
        return triggerFilters.Any(static value =>
            string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
    }

    private static int? NormalizePositive(int? value)
        => value is > 0 ? value : null;

    private static long? NormalizePositive(long? value)
        => value is > 0 ? value : null;

    private static bool RetentionPolicyEquals(RepositoryRetentionPolicyDto left, RepositoryRetentionPolicyDto right)
        => left.HasLocalOverride == right.HasLocalOverride
           && left.Enabled == right.Enabled
           && left.MaxAgeDays == right.MaxAgeDays
           && left.MaxSnapshots == right.MaxSnapshots
           && left.MaxTotalSizeBytes == right.MaxTotalSizeBytes
           && SetEquals(left.TriggerFilters.ToList(), right.TriggerFilters.ToList())
           && left.RunIntervalMinutes == right.RunIntervalMinutes
           && left.MaintenanceWindowStartHour == right.MaintenanceWindowStartHour
           && left.MaintenanceWindowEndHour == right.MaintenanceWindowEndHour
           && string.Equals(left.StorageMode, right.StorageMode, StringComparison.OrdinalIgnoreCase)
           && left.AllowManualSnapshotCleanup == right.AllowManualSnapshotCleanup
           && left.AutomaticCompactionEnabled == right.AutomaticCompactionEnabled
           && left.AutomaticCompactionWindowHours == right.AutomaticCompactionWindowHours
           && string.Equals(left.PolicySource, right.PolicySource, StringComparison.OrdinalIgnoreCase)
           && left.SourceRepositoryId == right.SourceRepositoryId;

    private static IReadOnlyCollection<string> NormalizeExcludedPatterns(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().Replace('\\', '/'))
            .Select(v => v.Trim('/'))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
