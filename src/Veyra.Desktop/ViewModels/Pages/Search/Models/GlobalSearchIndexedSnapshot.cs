using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;
using Veyra.Application.DTOs.Repository.Snapshots;

namespace Veyra.Desktop.ViewModels.Pages.Search;

internal sealed record GlobalSearchIndexedSnapshot(RepositoryDto Repository, RepositorySnapshotHistoryItemDto Snapshot);

internal sealed record GlobalSearchIndexedSnapshotFileChange(
    RepositoryDto Repository,
    GlobalSearchSnapshotResultItemViewModel Snapshot,
    RepositorySnapshotFileChangeDto Change);
