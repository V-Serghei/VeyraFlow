namespace Veyra.Application.DTOs.Repository.Comparison;

public sealed record RepositoryVersionPlanEntryDto(
    string RelativePath,
    string ChangeKind,
    bool ShouldCreateNewVersion,
    bool ShouldMarkIdentityDeleted);
