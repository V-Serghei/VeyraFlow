namespace Veyra.Application.DTOs;

public sealed record AccessTokenPolicyEvaluationDto(
    AccessTokenValidityState State,
    DateTime? ExpiresAtUtc,
    TimeSpan? RemainingLifetime,
    bool CanUseForSync,
    string Description);
