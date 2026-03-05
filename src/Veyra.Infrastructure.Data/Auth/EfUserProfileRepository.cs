using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Auth;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Auth;

public sealed class EfUserProfileRepository(VeyraDbContext db) : IUserProfileRepository
{
    public async Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var existing = await db.Set<UserProfile>()
            .FirstOrDefaultAsync(u => u.Username == username, ct);

        if (existing is not null)
        {
            existing.LastLoginAt = now;
            existing.IsActive = true;
            existing.UpdatedAt = now;
        }
        else
        {
            db.Add(new UserProfile
            {
                Username = username,
                LastLoginAt = now,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
