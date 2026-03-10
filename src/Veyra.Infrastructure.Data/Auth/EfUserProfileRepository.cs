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
                u.CloudUserId,
                u.AccessToken,
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
                u.CloudUserId,
                u.AccessToken,
                u.LastLoginAt))
            .ToListAsync(ct);
    }

    public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
        => SaveOrUpdateProfileAsync(username, null, null, ct);

    public async Task SaveOrUpdateProfileAsync(
        string username,
        long? cloudUserId,
        string? accessToken,
        CancellationToken ct = default)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
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
        }
        else
        {
            db.Add(new UserProfile
            {
                Username = normalizedUsername,
                CloudUserId = cloudUserId,
                AccessToken = accessToken,
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

    public async Task SignOutActiveAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.AccessToken, (string?)null)
                .SetProperty(u => u.UpdatedAt, now), ct);
    }
}
