namespace Veyra.Application.DTOs.Repository.Scanning;

public sealed record RepositoryScanProgressDto(
    string Stage,
    int Percent,
    int FilesProcessed,
    int FilesTotal,
    string Message);
