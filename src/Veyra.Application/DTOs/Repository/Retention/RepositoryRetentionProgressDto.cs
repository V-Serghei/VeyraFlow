namespace Veyra.Application.DTOs;

public sealed record RepositoryRetentionProgressDto(
    string Stage,
    int Percent,
    string Message);
