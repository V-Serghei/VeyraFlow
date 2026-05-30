namespace Veyra.Application.DTOs.Auth;

public sealed record AuthSessionDto(
    long CloudUserId,
    string Username,
    string? Email,
    string AccessToken,
    bool IsNewUser,
    DateTime? AccessTokenExpiresAtUtc = null,
    string? RefreshToken = null,
    DateTime? RefreshTokenExpiresAtUtc = null,
    long? CloudSessionId = null);
