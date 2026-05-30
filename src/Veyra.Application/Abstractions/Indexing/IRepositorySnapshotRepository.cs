using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;
using Veyra.Application.DTOs.PendingChanges;
using Veyra.Application.DTOs.Repository.Scanning;
using Veyra.Application.DTOs.Repository.Snapshots;
using Veyra.Application.DTOs.TextDiff;

namespace Veyra.Application.Abstractions.Indexing;

public interface IRepositorySnapshotRepository
{
    public Task<SnapshotSaveResultDto> SaveSnapshotAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        bool saveFileVersions = true,
        string? snapshotTitle = null,
        IReadOnlyCollection<string>? snapshotTags = null,
        IProgress<RepositoryScanProgressDto>? progress = null,
        CancellationToken ct = default,
        bool forceSnapshotCreation = false);

    public Task<SnapshotSaveResultDto> ApplyWorkingSnapshotDeltaAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken ct = default);

    public Task<SnapshotSaveResultDto> ApplyVersionedSnapshotDeltaAsync(
        int repositoryId,
        string trigger,
        DateTime scannedAtUtc,
        IReadOnlyCollection<RepositoryScanEntryDto> entries,
        IReadOnlyCollection<RepositoryScanEntryDto> upsertEntries,
        IReadOnlyCollection<string> removedPaths,
        CancellationToken ct = default);

    public Task<IReadOnlyList<RepositoryScanEntryDto>> GetLatestEntriesAsync(
        int repositoryId,
        CancellationToken ct = default);

    public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositoryScanEntryDto>>> GetLatestEntriesBatchAsync(
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken ct = default);

    public Task<IReadOnlyList<FileVersionInfoDto>> GetFileVersionsAsync(
        int repositoryId,
        string relativePath,
        int take = 50,
        CancellationToken ct = default);

    public Task<RepositoryPendingChangesDto> GetPendingChangesAsync(
        int repositoryId,
        int take = 200,
        CancellationToken ct = default);

    public Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> GetSnapshotHistoryAsync(
        int repositoryId,
        int take = 100,
        CancellationToken ct = default);

    public Task<IReadOnlyDictionary<int, IReadOnlyList<RepositorySnapshotHistoryItemDto>>> GetSnapshotHistoryBatchAsync(
        IReadOnlyCollection<int> repositoryIds,
        int take = 100,
        CancellationToken ct = default);

    public Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> GetSnapshotChangedFilesAsync(
        int repositoryId,
        long snapshotId,
        int take = 1000,
        CancellationToken ct = default);

    public Task<PendingFileDiffPreviewDto> GetPendingFileDiffPreviewAsync(
        int repositoryId,
        string relativePath,
        int maxLines = 3000,
        CancellationToken ct = default);

    public Task<PendingFileDiffPreviewDto> GetFileVersionDiffPreviewAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines = 3000,
        CancellationToken ct = default);

    public Task<TextDiffResultDto?> GetStoredTextDiffAsync(
        long leftFileVersionId,
        long rightFileVersionId,
        int maxLines,
        CancellationToken ct = default);

    public Task SaveStoredTextDiffAsync(
        TextDiffResultDto diff,
        int maxLines,
        CancellationToken ct = default);

    public Task<FileVersionRestoreDto?> GetFileVersionRestoreDataAsync(
        long fileVersionId,
        CancellationToken ct = default);

    public Task<RepositorySnapshotRestoreDataDto?> GetSnapshotRestoreDataAsync(
        int repositoryId,
        long snapshotId,
        CancellationToken ct = default);
}
