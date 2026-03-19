using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.State;

public sealed class RepositoryDashboardFilterStore(
    ILogger<RepositoryDashboardFilterStore> log)
    : IRepositoryDashboardFilterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string ResolveFilePath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "state");

        Directory.CreateDirectory(root);
        return Path.Combine(root, "repository-dashboard-filters.json");
    }

    public async Task<IReadOnlyList<RepositoryDashboardFilterPreset>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            if (!File.Exists(path))
                return Array.Empty<RepositoryDashboardFilterPreset>();

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var parsed = await JsonSerializer.DeserializeAsync<List<RepositoryDashboardFilterPreset>>(stream, JsonOptions, ct);
            if (parsed is null || parsed.Count == 0)
                return Array.Empty<RepositoryDashboardFilterPreset>();

            return parsed
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new RepositoryDashboardFilterPreset
                {
                    Name = p.Name.Trim(),
                    SearchQuery = p.SearchQuery ?? string.Empty,
                    AvailabilityFilter = NormalizeAvailability(p.AvailabilityFilter),
                    SyncStateFilter = NormalizeSyncState(p.SyncStateFilter),
                    FormatTagFilter = NormalizeFormatTag(p.FormatTagFilter),
                    MinSizeMb = p.MinSizeMb ?? string.Empty,
                    MaxSizeMb = p.MaxSizeMb ?? string.Empty,
                    OnlyQueueIssues = p.OnlyQueueIssues,
                    SavedAtUtc = p.SavedAtUtc == default ? DateTime.UtcNow : p.SavedAtUtc
                })
                .OrderByDescending(p => p.SavedAtUtc)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load repository dashboard filter presets.");
            return Array.Empty<RepositoryDashboardFilterPreset>();
        }
    }

    public async Task SaveAsync(IReadOnlyList<RepositoryDashboardFilterPreset> presets, CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            var normalized = presets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new RepositoryDashboardFilterPreset
                {
                    Name = p.Name.Trim(),
                    SearchQuery = p.SearchQuery ?? string.Empty,
                    AvailabilityFilter = NormalizeAvailability(p.AvailabilityFilter),
                    SyncStateFilter = NormalizeSyncState(p.SyncStateFilter),
                    FormatTagFilter = NormalizeFormatTag(p.FormatTagFilter),
                    MinSizeMb = p.MinSizeMb ?? string.Empty,
                    MaxSizeMb = p.MaxSizeMb ?? string.Empty,
                    OnlyQueueIssues = p.OnlyQueueIssues,
                    SavedAtUtc = p.SavedAtUtc == default ? DateTime.UtcNow : p.SavedAtUtc
                })
                .OrderByDescending(p => p.SavedAtUtc)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to save repository dashboard filter presets.");
        }
    }

    private static string NormalizeAvailability(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "available" or "unavailable" ? normalized : "all";
    }

    private static string NormalizeSyncState(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "synced" => "synced",
            "queued" => "queued",
            "syncing" => "syncing",
            "retrying" => "retrying",
            "conflict" => "conflict",
            "auth_required" => "auth_required",
            "dead_letter" => "dead_letter",
            "failed" => "failed",
            _ => "all"
        };
    }

    private static string NormalizeFormatTag(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "all"
            : value.Trim().ToLowerInvariant();
}
