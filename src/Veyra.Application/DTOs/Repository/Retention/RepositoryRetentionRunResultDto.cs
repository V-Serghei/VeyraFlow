namespace Veyra.Application.DTOs;

public sealed record RepositoryRetentionRunResultDto(
    int RepositoryId,
    bool DryRun,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    bool PolicyApplied,
    int SnapshotsMarked,
    int SnapshotEntriesMarked,
    int SnapshotLinksMarked,
    int FileVersionsMarked,
    int FileVersionBlocksMarked,
    int FileIdentitiesMarked,
    int DiffsMarked,
    int DiffHunksMarked,
    int DiffLinesMarked,
    int TextLineAtomsDeleted,
    int BlockFilesDeleted,
    long EstimatedFreedBytes,
    string Summary);
