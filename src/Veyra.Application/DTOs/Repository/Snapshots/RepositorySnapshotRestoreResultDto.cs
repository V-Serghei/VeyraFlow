namespace Veyra.Application.DTOs.Repository.Snapshots;

public sealed record RepositorySnapshotRestoreResultDto(
    int RepositoryId,
    long SourceSnapshotId,
    string Mode,
    int RestoredFilesCount,
    int OverwrittenFilesCount,
    int BackedUpFilesCount,
    bool CreatedSnapshot,
    string? BackupDirectoryPath,
    string? RestoreDirectoryPath);
