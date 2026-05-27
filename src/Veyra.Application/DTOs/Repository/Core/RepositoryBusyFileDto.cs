namespace Veyra.Application.DTOs;

public sealed record RepositoryBusyFileDto(
    string RelativePath,
    string FullPath,
    string Error,
    string SuggestedAction);
