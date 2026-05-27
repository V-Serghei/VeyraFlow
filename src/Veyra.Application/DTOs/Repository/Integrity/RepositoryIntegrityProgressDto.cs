namespace Veyra.Application.DTOs;

public sealed record RepositoryIntegrityProgressDto(
    string Stage,
    int Percent,
    string Message);
