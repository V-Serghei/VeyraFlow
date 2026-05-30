namespace Veyra.Application.DTOs.Repository.Comparison;

public sealed record RepositoryVersionPlanningResultDto(
    int ChangedFilesCount,
    int NewVersionsCount,
    IReadOnlyList<RepositoryVersionPlanEntryDto> Entries);
