using Microsoft.EntityFrameworkCore;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.OperationJournal;
using Veyra.Domain.Entities;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data.Observability;

public sealed class EfOperationJournalService(VeyraDbContext db) : IOperationJournalService
{
    public async Task AppendAsync(OperationJournalEntryDto entry, CancellationToken ct = default)
    {
        var entity = new OperationJournalEntry
        {
            OccurredAtUtc = entry.OccurredAtUtc == default ? DateTime.UtcNow : entry.OccurredAtUtc,
            Level = TruncateRequired(entry.Level, 16, "info"),
            Category = TruncateRequired(entry.Category, 64, "command"),
            Action = TruncateRequired(entry.Action, 128, "unknown"),
            RepositoryId = entry.RepositoryId,
            Username = TruncateOptional(entry.Username, 128),
            Message = TruncateRequired(entry.Message, 2048, "Operation completed."),
            Details = TruncateOptional(entry.Details, 4096)
        };

        db.Set<OperationJournalEntry>().Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OperationJournalEntryDto>> GetRecentAsync(int take = 100, CancellationToken ct = default)
    {
        var normalizedTake = Math.Clamp(take, 1, 500);

        return await db.Set<OperationJournalEntry>()
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(normalizedTake)
            .Select(x => new OperationJournalEntryDto(
                x.Id,
                x.OccurredAtUtc,
                x.Level,
                x.Category,
                x.Action,
                x.RepositoryId,
                x.Username,
                x.Message,
                x.Details))
            .ToListAsync(ct);
    }

    private static string TruncateRequired(string? value, int maxLength, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
        if (source.Length <= maxLength)
            return source;

        return source[..maxLength];
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var source = value.Trim();
        return source.Length <= maxLength
            ? source
            : source[..maxLength];
    }
}
