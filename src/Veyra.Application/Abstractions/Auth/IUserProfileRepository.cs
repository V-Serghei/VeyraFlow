using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Auth;

public interface IUserProfileRepository
{
    Task<string?> GetActiveUsernameAsync(CancellationToken ct = default);
    Task<UserProfileSessionDto?> GetActiveProfileAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserProfileSessionDto>> GetProfilesAsync(CancellationToken ct = default);
    Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default);
    Task SaveOrUpdateProfileAsync(string username, long? cloudUserId, string? accessToken, CancellationToken ct = default);
    Task<bool> SetActiveProfileAsync(string username, CancellationToken ct = default);
    Task SignOutActiveAsync(CancellationToken ct = default);
}
