namespace Veyra.Application.DTOs;

public sealed record UserProfileSessionDto(
    string Username,
    long? CloudUserId,
    string? AccessToken,
    DateTime LastLoginAtUtc);
