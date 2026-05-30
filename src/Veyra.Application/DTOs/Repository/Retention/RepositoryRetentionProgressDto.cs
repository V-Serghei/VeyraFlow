namespace Veyra.Application.DTOs.Repository.Retention;

public sealed record RepositoryRetentionProgressDto(
    string Stage,
    int Percent,
    string Message);
