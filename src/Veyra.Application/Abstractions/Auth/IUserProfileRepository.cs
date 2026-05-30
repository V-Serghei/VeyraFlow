using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Auth;

namespace Veyra.Application.Abstractions.Auth;

public interface IUserProfileRepository
{
    public Task<string?> GetActiveUsernameAsync(CancellationToken ct = default);
    public Task<UserProfileSessionDto?> GetActiveProfileAsync(CancellationToken ct = default);
    public Task<IReadOnlyList<UserProfileSessionDto>> GetProfilesAsync(CancellationToken ct = default);
    public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default);

    public Task SaveOrUpdateProfileAsync(
        string username,
        long? cloudUserId,
        string? accessToken,
        string? email,
        long? cloudSessionId,
        string? refreshToken,
        DateTime? accessTokenExpiresAtUtc,
        DateTime? refreshTokenExpiresAtUtc,
        CancellationToken ct = default);

    public Task<bool> SetActiveProfileAsync(string username, CancellationToken ct = default);
    public Task<bool> SetRequirePasswordForSensitiveActionsAsync(bool enabled, CancellationToken ct = default);
    public Task SignOutActiveAsync(CancellationToken ct = default);
}
