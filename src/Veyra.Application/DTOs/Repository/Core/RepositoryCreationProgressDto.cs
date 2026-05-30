namespace Veyra.Application.DTOs.Repository.Core;

public sealed record RepositoryCreationProgressDto(
    string Stage,
    int Percent,
    int FilesProcessed,
    int FilesTotal,
    string Message);
