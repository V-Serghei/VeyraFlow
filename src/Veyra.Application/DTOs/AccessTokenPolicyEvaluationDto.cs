namespace Veyra.Application.DTOs;

public enum AccessTokenValidityState
{
    Missing = 0,
    Invalid = 1,
    Expired = 2,
    ExpiringSoon = 3,
    Valid = 4
}

public sealed record AccessTokenPolicyEvaluationDto(
    AccessTokenValidityState State,
    DateTime? ExpiresAtUtc,
    TimeSpan? RemainingLifetime,
    bool CanUseForSync,
    string Description);
