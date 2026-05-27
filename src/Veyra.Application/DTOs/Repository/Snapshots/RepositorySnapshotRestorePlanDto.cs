namespace Veyra.Application.DTOs;

public sealed record RepositorySnapshotRestorePlanDto(
    int RepositoryId,
    long SnapshotId,
    string? SnapshotTitle,
    DateTime SnapshotCreatedAtUtc,
    int FilesToRestoreCount,
    int FilesToChangeCount,
    int FilesToMoveToBackupCount,
    IReadOnlyList<string> MissingBlockKeys)
{
    public int MissingBlockCount => MissingBlockKeys.Count;
    public bool CanRestore => MissingBlockKeys.Count == 0;
}
