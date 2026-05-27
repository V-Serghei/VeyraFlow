namespace Veyra.Application.DTOs;

public sealed record RepositoryCreationProgressDto(
    string Stage,
    int Percent,
    int FilesProcessed,
    int FilesTotal,
    string Message);
