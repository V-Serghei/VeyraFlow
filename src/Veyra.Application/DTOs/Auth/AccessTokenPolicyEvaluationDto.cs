namespace Veyra.Application.DTOs.Auth;

public sealed record AccessTokenPolicyEvaluationDto(
    AccessTokenValidityState State,
    DateTime? ExpiresAtUtc,
    TimeSpan? RemainingLifetime,
    bool CanUseForSync,
    string Description);
