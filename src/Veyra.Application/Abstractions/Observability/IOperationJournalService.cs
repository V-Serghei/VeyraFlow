using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Observability;

public interface IOperationJournalService
{
    Task AppendAsync(OperationJournalEntryDto entry, CancellationToken ct = default);
    Task<IReadOnlyList<OperationJournalEntryDto>> GetRecentAsync(int take = 100, CancellationToken ct = default);
}
