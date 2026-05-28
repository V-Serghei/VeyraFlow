using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Pages.Search;

internal sealed record GlobalSearchIndexedSnapshot(RepositoryDto Repository, RepositorySnapshotHistoryItemDto Snapshot);

internal sealed record GlobalSearchIndexedSnapshotFileChange(
    RepositoryDto Repository,
    GlobalSearchSnapshotResultItemViewModel Snapshot,
    RepositorySnapshotFileChangeDto Change);
