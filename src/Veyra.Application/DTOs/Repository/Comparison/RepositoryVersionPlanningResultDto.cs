namespace Veyra.Application.DTOs;

public sealed record RepositoryVersionPlanningResultDto(
    int ChangedFilesCount,
    int NewVersionsCount,
    IReadOnlyList<RepositoryVersionPlanEntryDto> Entries);
