namespace Veyra.Infrastructure.Sync.Auth;

internal sealed record AuthResponse(
    bool Ok,
    long UserId,
    string Username,
    string? Email,
    long? SessionId,
    string AccessToken,
    string? RefreshToken,
    bool IsNewUser,
    System.DateTime? ExpiresAtUtc,
    System.DateTime? RefreshExpiresAtUtc,
    string? Message);
