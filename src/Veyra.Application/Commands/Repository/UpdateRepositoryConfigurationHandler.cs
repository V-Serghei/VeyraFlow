using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
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
        try
        {
            log.LogInformation("Updating repository configuration {RepositoryId}", request.RepositoryId);

            var repo = await repositories.GetRepositoryByIdAsync(request.RepositoryId, ct);
            if (repo is null || repo.IsDeleted)
                return OperationResult.Fail("??????????? ?? ??????.");

            var normalizedPath = NormalizeDirectoryPath(request.DirectoryPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
                return OperationResult.Fail("????????? ?????????? ?? ??????????.");

            var normalizedFormats = NormalizeFormats(request.Formats);
            if (normalizedFormats.Count == 0)
                return OperationResult.Fail("?? ?????? ?? ???? ??????.");

            var safeName = string.IsNullOrWhiteSpace(request.Name)
                ? repo.Name
                : request.Name.Trim();

            var safeRetentionPolicy = NormalizeRetentionPolicy(request.RetentionPolicy);

            if (!PathEquals(repo.DirectoryPath, normalizedPath))
                await setup.UpdateWatchedDirectoryAsync(repo.DirectoryPath, normalizedPath, ct);

            await repositories.UpdateRepositoryAsync(
                repo.Id,
                safeName,
                request.Description,
                safeRetentionPolicy,
                ct);

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

            await scanner.ScanRepositoryAsync(
                repo.Id,
                null,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: false,
                    TriggerOverride: "sync_index_config_update"),
                ct);

            await native.ApplySetupAsync(ct);

            log.LogInformation(
                "Repository {RepositoryId} updated. Path {Path}. Formats {FormatCount}. RetentionEnabled {RetentionEnabled}",
                repo.Id,
                normalizedPath,
                normalizedFormats.Count,
                safeRetentionPolicy.Enabled);

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

    private static List<string> NormalizeFormats(IEnumerable<string> values)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Select(v => v.StartsWith('.') ? v : "." + v)
            .Select(v => v.ToLowerInvariant())
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
            RunIntervalMinutes = Math.Clamp(policy.RunIntervalMinutes, 5, 7 * 24 * 60)
        };
    }

    private static int? NormalizePositive(int? value)
        => value is > 0 ? value : null;

    private static long? NormalizePositive(long? value)
        => value is > 0 ? value : null;
}
