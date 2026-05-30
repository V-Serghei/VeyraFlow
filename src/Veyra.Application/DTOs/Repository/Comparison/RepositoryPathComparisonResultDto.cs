using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.DTOs.Repository.Comparison;

public sealed record RepositoryPathComparisonResultDto(
    int AddedCount,
    int ModifiedCount,
    int DeletedCount,
    int ChangedFilesCount,
    IReadOnlyList<RepositoryPendingChangeEntryDto> Entries);
