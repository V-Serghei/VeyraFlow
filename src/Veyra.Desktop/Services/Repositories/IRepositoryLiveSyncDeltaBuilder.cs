using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.DTOs;
using Veyra.Desktop.Services.Sync;

namespace Veyra.Desktop.Services.Repositories;

public interface IRepositoryLiveSyncDeltaBuilder
{
    Task<RepositoryLiveSyncDeltaBuildResult> BuildAsync(
        string repositoryRootPath,
        IReadOnlyCollection<string> linkedFormats,
        IReadOnlyCollection<string> excludedPatterns,
        IReadOnlyList<RepositoryScanEntryDto> currentEntries,
        IReadOnlyList<RepositoryFsEventLeaseItem> events,
        CancellationToken ct = default);
}

public sealed record RepositoryLiveSyncDeltaBuildResult(
    bool CanApplyIncrementally,
    bool HasMeaningfulChanges,
    IReadOnlyList<RepositoryScanEntryDto> UpdatedEntries,
    IReadOnlyList<RepositoryScanEntryDto> UpsertEntries,
    IReadOnlyList<string> RemovedPaths,
    string? FallbackReason = null)
{
    public static RepositoryLiveSyncDeltaBuildResult Fallback(string reason) =>
        new(
            CanApplyIncrementally: false,
            HasMeaningfulChanges: false,
            UpdatedEntries: [],
            UpsertEntries: [],
            RemovedPaths: [],
            FallbackReason: reason);

    public static RepositoryLiveSyncDeltaBuildResult NoChanges(IReadOnlyList<RepositoryScanEntryDto> entries) =>
        new(
            CanApplyIncrementally: true,
            HasMeaningfulChanges: false,
            UpdatedEntries: entries,
            UpsertEntries: [],
            RemovedPaths: []);
}
