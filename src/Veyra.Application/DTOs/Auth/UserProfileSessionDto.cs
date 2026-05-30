namespace Veyra.Application.DTOs.Auth;

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
