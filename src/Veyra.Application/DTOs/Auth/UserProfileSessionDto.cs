namespace Veyra.Application.DTOs;

public sealed record UserProfileSessionDto(
    string Username,
    string? Email,
    long? CloudUserId,
    long? CloudSessionId,
    string? AccessToken,
    DateTime? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    DateTime? RefreshTokenExpiresAtUtc,
    bool RequirePasswordForSensitiveActions,
    DateTime LastLoginAtUtc);
