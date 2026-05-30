namespace Veyra.Application.DTOs.Repository.Core;

public sealed record RepositoryBusyFileDto(
    string RelativePath,
    string FullPath,
    string Error,
    string SuggestedAction);
