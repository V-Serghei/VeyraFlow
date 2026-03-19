namespace Veyra.Application.DTOs;

public sealed record RepositoryPathComparisonResultDto(
    int AddedCount,
    int ModifiedCount,
    int DeletedCount,
    int ChangedFilesCount,
    IReadOnlyList<RepositoryPendingChangeEntryDto> Entries);
