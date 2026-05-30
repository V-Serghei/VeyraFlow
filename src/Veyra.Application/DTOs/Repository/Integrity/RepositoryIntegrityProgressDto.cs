namespace Veyra.Application.DTOs.Repository.Integrity;

public sealed record RepositoryIntegrityProgressDto(
    string Stage,
    int Percent,
    string Message);
