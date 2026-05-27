using Veyra.Application.Abstractions.Observability;
using Veyra.Application.DTOs;
using Veyra.Domain.Observability;

namespace Veyra.Infrastructure.Data.Observability;

public sealed class RuntimeAwareOperationJournalService(
    EfOperationJournalService inner,
    IRuntimeObservabilityState observability)
    : IOperationJournalService
{
    public Task AppendAsync(OperationJournalEntryDto entry, CancellationToken ct = default)
    {
        if (!observability.IsDiagnosticsEnabled)
            return Task.CompletedTask;

        return inner.AppendAsync(entry, ct);
    }

    public Task<IReadOnlyList<OperationJournalEntryDto>> GetRecentAsync(int take = 100, CancellationToken ct = default)
        => inner.GetRecentAsync(take, ct);
}
