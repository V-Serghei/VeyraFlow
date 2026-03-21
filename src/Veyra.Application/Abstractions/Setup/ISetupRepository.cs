﻿namespace Veyra.Application.Abstractions.Setup;

public interface ISetupRepository
{
    // ── Bulk (initial setup) ───────────────────────────────────────
    Task SaveInitialSetupAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> extensions, CancellationToken ct = default);

    // ── Directories CRUD ───────────────────────────────────────────
    Task ReplaceWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
    Task AddWatchedDirectoryAsync(string path, CancellationToken ct = default);
    Task UpdateWatchedDirectoryAsync(string oldPath, string newPath, CancellationToken ct = default);
    Task DeleteWatchedDirectoriesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetWatchedDirectoriesAsync(CancellationToken ct = default);

    // ── Formats CRUD ───────────────────────────────────────────────
    Task ReplaceTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default);
    Task AddTrackedExtensionAsync(string extension, CancellationToken ct = default);
    Task UpdateTrackedExtensionAsync(string oldPattern, string newPattern, CancellationToken ct = default);
    Task DeleteTrackedExtensionsAsync(IReadOnlyCollection<string> extensions, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTrackedExtensionsAsync(CancellationToken ct = default);

    // ── Directory ↔ Format links (many-to-many) ───────────────────
    Task LinkDirectoryToFormatsAsync(string directoryPath, IReadOnlyCollection<string> formatPatterns, CancellationToken ct = default);
    Task UnlinkDirectoryFromFormatsAsync(string directoryPath, IReadOnlyCollection<string> formatPatterns, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetFormatsForDirectoryAsync(string directoryPath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDirectoriesForFormatAsync(string formatPattern, CancellationToken ct = default);
    Task<IReadOnlyList<(string DirectoryPath, string FormatPattern)>> GetAllDirectoryFormatLinksAsync(CancellationToken ct = default);
}
