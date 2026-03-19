using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.DTOs;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Auth;

public sealed class EfUserProfileRepository(VeyraDbContext db) : IUserProfileRepository
{
    public async Task<string?> GetActiveUsernameAsync(CancellationToken ct = default)
    {
        return await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<UserProfileSessionDto?> GetActiveProfileAsync(CancellationToken ct = default)
    {
        return await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .Select(u => new UserProfileSessionDto(
                u.Username,
                u.Email,
                u.CloudUserId,
                u.CloudSessionId,
                u.AccessToken,
                u.AccessTokenExpiresAtUtc,
                u.RefreshToken,
                u.RefreshTokenExpiresAtUtc,
                u.RequirePasswordForSensitiveActions,
                u.LastLoginAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserProfileSessionDto>> GetProfilesAsync(CancellationToken ct = default)
    {
        return await db.Set<UserProfile>()
            .OrderByDescending(u => u.IsActive)
            .ThenByDescending(u => u.LastLoginAt)
            .Select(u => new UserProfileSessionDto(
                u.Username,
                u.Email,
                u.CloudUserId,
                u.CloudSessionId,
                u.AccessToken,
                u.AccessTokenExpiresAtUtc,
                u.RefreshToken,
                u.RefreshTokenExpiresAtUtc,
                u.RequirePasswordForSensitiveActions,
                u.LastLoginAt))
            .ToListAsync(ct);
    }

    public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
        => SaveOrUpdateProfileAsync(username, null, null, null, null, null, null, null, ct);

    public async Task SaveOrUpdateProfileAsync(
        string username,
        long? cloudUserId,
        string? accessToken,
        string? email,
        long? cloudSessionId,
        string? refreshToken,
        DateTime? accessTokenExpiresAtUtc,
        DateTime? refreshTokenExpiresAtUtc,
        CancellationToken ct = default)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        var normalizedEmail = string.IsNullOrWhiteSpace(email)
            ? null
            : email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return;

        var now = DateTime.UtcNow;

        await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.UpdatedAt, now), ct);

        var existing = await db.Set<UserProfile>()
            .FirstOrDefaultAsync(u => u.Username == normalizedUsername, ct);

        if (existing is not null)
        {
            existing.LastLoginAt = now;
            existing.IsActive = true;
            existing.UpdatedAt = now;
            existing.CloudUserId = cloudUserId ?? existing.CloudUserId;
            existing.AccessToken = accessToken ?? existing.AccessToken;
            existing.Email = normalizedEmail ?? existing.Email;
            existing.CloudSessionId = cloudSessionId ?? existing.CloudSessionId;
            existing.RefreshToken = refreshToken ?? existing.RefreshToken;
            existing.AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc ?? existing.AccessTokenExpiresAtUtc;
            existing.RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc ?? existing.RefreshTokenExpiresAtUtc;
        }
        else
        {
            db.Add(new UserProfile
            {
                Username = normalizedUsername,
                Email = normalizedEmail,
                CloudUserId = cloudUserId,
                CloudSessionId = cloudSessionId,
                AccessToken = accessToken,
                AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
                RefreshToken = refreshToken,
                RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc,
                LastLoginAt = now,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetActiveProfileAsync(string username, CancellationToken ct = default)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return false;

        var target = await db.Set<UserProfile>()
            .FirstOrDefaultAsync(u => u.Username == normalizedUsername, ct);

        if (target is null)
            return false;

        var now = DateTime.UtcNow;

        await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.UpdatedAt, now), ct);

        target.IsActive = true;
        target.LastLoginAt = now;
        target.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetRequirePasswordForSensitiveActionsAsync(bool enabled, CancellationToken ct = default)
    {
        var active = await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .FirstOrDefaultAsync(ct);

        if (active is null)
            return false;

        active.RequirePasswordForSensitiveActions = enabled;
        active.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SignOutActiveAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.AccessToken, (string?)null)
                .SetProperty(u => u.CloudSessionId, (long?)null)
                .SetProperty(u => u.AccessTokenExpiresAtUtc, (DateTime?)null)
                .SetProperty(u => u.RefreshToken, (string?)null)
                .SetProperty(u => u.RefreshTokenExpiresAtUtc, (DateTime?)null)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }
}
