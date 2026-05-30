namespace Veyra.Application.DTOs.Repository.Core;

public sealed record RepositoryCreationOutcomeDto(
    int RepositoryId,
    IReadOnlyList<RepositoryBusyFileDto>? BusyFiles = null)
{
    public IReadOnlyList<RepositoryBusyFileDto> BusyFilesSafe { get; } =
        BusyFiles ?? Array.Empty<RepositoryBusyFileDto>();

    public int BusyFilesCount => BusyFilesSafe.Count;
    public bool HasBusyFiles => BusyFilesCount > 0;
}
