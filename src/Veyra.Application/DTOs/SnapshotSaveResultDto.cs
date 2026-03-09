namespace Veyra.Application.DTOs;

public sealed record SnapshotSaveResultDto(
    bool SnapshotCreated,
    bool NoChangesDetected)
{
    public static SnapshotSaveResultDto Created()
        => new(true, false);

    public static SnapshotSaveResultDto NoChanges()
        => new(false, true);

    public static SnapshotSaveResultDto Skipped()
        => new(false, false);
}
