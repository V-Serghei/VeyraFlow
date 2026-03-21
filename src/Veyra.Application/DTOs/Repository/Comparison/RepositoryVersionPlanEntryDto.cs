namespace Veyra.Application.DTOs;

public sealed record RepositoryVersionPlanEntryDto(
    string RelativePath,
    string ChangeKind,
    bool ShouldCreateNewVersion,
    bool ShouldMarkIdentityDeleted);
