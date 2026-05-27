namespace Veyra.Application.DTOs;

public sealed record RepositoryRecoveryResultDto(
    int RepositoryId,
    string Action,
    bool Success,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    int AffectedRows,
    IReadOnlyList<string> Messages,
    string Summary);
