namespace Veyra.Application.DTOs.PendingChanges;

public sealed record RepositoryPendingChangesDto(
    DateTime? BaselineSnapshotAtUtc,
    int AddedCount,
    int ModifiedCount,
    int DeletedCount,
    IReadOnlyList<RepositoryPendingChangeEntryDto> Entries)
{
    public static RepositoryPendingChangesDto Empty { get; } =
        new(null, 0, 0, 0, Array.Empty<RepositoryPendingChangeEntryDto>());
}
