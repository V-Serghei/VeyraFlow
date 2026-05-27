using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.State;

public sealed class RepositoryExplorerFilterStore(
    ILogger<RepositoryExplorerFilterStore> log)
    : IRepositoryExplorerFilterStore
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
        return Path.Combine(root, "repository-explorer-filters.json");
    }

    public async Task<IReadOnlyList<RepositoryExplorerFilterPreset>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            if (!File.Exists(path))
                return Array.Empty<RepositoryExplorerFilterPreset>();

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            var parsed = await JsonSerializer.DeserializeAsync<List<RepositoryExplorerFilterPreset>>(stream, JsonOptions, ct);
            if (parsed is null || parsed.Count == 0)
                return Array.Empty<RepositoryExplorerFilterPreset>();

            return parsed
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new RepositoryExplorerFilterPreset
                {
                    Name = p.Name.Trim(),
                    SearchQuery = p.SearchQuery ?? string.Empty,
                    EntryTypeFilter = NormalizeEntryType(p.EntryTypeFilter),
                    ExtensionFilter = NormalizeExtension(p.ExtensionFilter),
                    ModifiedWindowFilter = NormalizeModifiedWindow(p.ModifiedWindowFilter),
                    MinSizeMb = p.MinSizeMb ?? string.Empty,
                    MaxSizeMb = p.MaxSizeMb ?? string.Empty,
                    SavedAtUtc = p.SavedAtUtc == default ? DateTime.UtcNow : p.SavedAtUtc
                })
                .OrderByDescending(p => p.SavedAtUtc)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load repository explorer filter presets.");
            return Array.Empty<RepositoryExplorerFilterPreset>();
        }
    }

    public async Task SaveAsync(IReadOnlyList<RepositoryExplorerFilterPreset> presets, CancellationToken ct = default)
    {
        try
        {
            var path = ResolveFilePath();
            var normalized = presets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new RepositoryExplorerFilterPreset
                {
                    Name = p.Name.Trim(),
                    SearchQuery = p.SearchQuery ?? string.Empty,
                    EntryTypeFilter = NormalizeEntryType(p.EntryTypeFilter),
                    ExtensionFilter = NormalizeExtension(p.ExtensionFilter),
                    ModifiedWindowFilter = NormalizeModifiedWindow(p.ModifiedWindowFilter),
                    MinSizeMb = p.MinSizeMb ?? string.Empty,
                    MaxSizeMb = p.MaxSizeMb ?? string.Empty,
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
            log.LogWarning(ex, "Failed to save repository explorer filter presets.");
        }
    }

    private static string NormalizeEntryType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "files" or "folders" ? normalized : "all";
    }

    private static string NormalizeExtension(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "all")
            return "all";

        return normalized.StartsWith('.') ? normalized : "." + normalized;
    }

    private static string NormalizeModifiedWindow(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "24h" or "7d" or "30d" ? normalized : "all";
    }
}
