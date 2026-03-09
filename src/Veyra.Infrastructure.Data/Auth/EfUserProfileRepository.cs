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
        var profile = await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .OrderByDescending(u => u.LastLoginAt)
            .Select(u => new UserProfileSessionDto(
                u.Username,
                u.CloudUserId,
                u.AccessToken,
                u.LastLoginAt))
            .FirstOrDefaultAsync(ct);

        return profile;
    }

    public Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
        => SaveOrUpdateProfileAsync(username, null, null, ct);

    public async Task SaveOrUpdateProfileAsync(
        string username,
        long? cloudUserId,
        string? accessToken,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        await db.Set<UserProfile>()
            .Where(u => u.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.UpdatedAt, now), ct);

        var existing = await db.Set<UserProfile>()
            .FirstOrDefaultAsync(u => u.Username == username, ct);

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
                Username = username,
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
}
