namespace Veyra.Application.DTOs;

public sealed record AuthSessionDto(
    long CloudUserId,
    string Username,
    string AccessToken,
    bool IsNewUser);
