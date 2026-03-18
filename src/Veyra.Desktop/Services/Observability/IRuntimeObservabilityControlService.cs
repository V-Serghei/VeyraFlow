using System.Threading;
using System.Threading.Tasks;
using Veyra.Domain.Observability;

namespace Veyra.Desktop.Services.Observability;

public interface IRuntimeObservabilityControlService : IRuntimeObservabilityState
{
    RuntimeObservabilitySnapshot Snapshot { get; }

    Task SetDiagnosticsEnabledAsync(bool enabled, CancellationToken ct = default);

    Task SetLoggingEnabledAsync(bool enabled, CancellationToken ct = default);
}
