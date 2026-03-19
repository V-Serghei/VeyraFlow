namespace Veyra.Application.DTOs;

public sealed record RepositoryScanProgressDto(
    string Stage,
    int Percent,
    int FilesProcessed,
    int FilesTotal,
    string Message);
