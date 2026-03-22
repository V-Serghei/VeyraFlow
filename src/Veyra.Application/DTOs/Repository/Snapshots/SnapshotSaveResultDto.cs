namespace Veyra.Application.DTOs;

public sealed record SnapshotSaveResultDto(
    bool SnapshotCreated,
    bool NoChangesDetected,
    IReadOnlyList<RepositoryBusyFileDto>? BusyFiles = null)
{
    public IReadOnlyList<RepositoryBusyFileDto> BusyFilesSafe { get; } =
        BusyFiles ?? Array.Empty<RepositoryBusyFileDto>();

    public int BusyFilesCount => BusyFilesSafe.Count;
    public bool HasBusyFiles => BusyFilesCount > 0;

    public static SnapshotSaveResultDto Created(IReadOnlyList<RepositoryBusyFileDto>? busyFiles = null)
        => new(true, false, busyFiles);

    public static SnapshotSaveResultDto NoChanges()
        => new(false, true, Array.Empty<RepositoryBusyFileDto>());

    public static SnapshotSaveResultDto Skipped()
        => new(false, false, Array.Empty<RepositoryBusyFileDto>());
}
