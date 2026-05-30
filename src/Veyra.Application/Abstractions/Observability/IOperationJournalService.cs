using Veyra.Application.DTOs;
using Veyra.Application.DTOs.OperationJournal;

namespace Veyra.Application.Abstractions.Observability;

public interface IOperationJournalService
{
    public Task AppendAsync(OperationJournalEntryDto entry, CancellationToken ct = default);
    public Task<IReadOnlyList<OperationJournalEntryDto>> GetRecentAsync(int take = 100, CancellationToken ct = default);
}
