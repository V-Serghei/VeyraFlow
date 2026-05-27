using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.State;

public sealed class GlobalSearchFilterStore(
    ILogger<GlobalSearchFilterStore> log)
    : IGlobalSearchFilterStore
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
        return Path.Combine(root, "global-search-filters.json");
    }

    public async Task<IReadOnlyList<GlobalSearchFilterPreset>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            if (!File.Exists(path))
                return Array.Empty<GlobalSearchFilterPreset>();

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var parsed = await JsonSerializer.DeserializeAsync<List<GlobalSearchFilterPreset>>(stream, JsonOptions, ct);
            if (parsed is null || parsed.Count == 0)
                return Array.Empty<GlobalSearchFilterPreset>();

            return parsed
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(NormalizePreset)
                .OrderByDescending(p => p.SavedAtUtc)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load global search filter presets.");
            return Array.Empty<GlobalSearchFilterPreset>();
        }
    }

    public async Task SaveAsync(IReadOnlyList<GlobalSearchFilterPreset> presets, CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            var normalized = presets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(NormalizePreset)
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
            log.LogWarning(ex, "Failed to save global search filter presets.");
        }
    }

    private static GlobalSearchFilterPreset NormalizePreset(GlobalSearchFilterPreset preset)
        => new()
        {
            Name = preset.Name.Trim(),
            SearchQuery = preset.SearchQuery ?? string.Empty,
            RepositoryId = preset.RepositoryId is > 0 ? preset.RepositoryId : null,
            TrackedFormatFilter = NormalizeFilter(preset.TrackedFormatFilter),
            EntryTypeFilter = NormalizeEntryType(preset.EntryTypeFilter),
            ExtensionFilter = NormalizeExtension(preset.ExtensionFilter),
            ModifiedWindowFilter = NormalizeModifiedWindow(preset.ModifiedWindowFilter),
            SnapshotTagFilter = NormalizeFilter(preset.SnapshotTagFilter),
            MinSizeMb = preset.MinSizeMb ?? string.Empty,
            MaxSizeMb = preset.MaxSizeMb ?? string.Empty,
            SavedAtUtc = preset.SavedAtUtc == default ? DateTime.UtcNow : preset.SavedAtUtc
        };

    private static string NormalizeFilter(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "all" : normalized;
    }

    private static string NormalizeEntryType(string? value)
    {
        var normalized = NormalizeFilter(value);
        return normalized is "files" or "folders" ? normalized : "all";
    }

    private static string NormalizeExtension(string? value)
    {
        var normalized = NormalizeFilter(value);
        if (normalized == "all")
            return "all";

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string NormalizeModifiedWindow(string? value)
    {
        var normalized = NormalizeFilter(value);
        return normalized is "24h" or "7d" or "30d" ? normalized : "all";
    }
}
