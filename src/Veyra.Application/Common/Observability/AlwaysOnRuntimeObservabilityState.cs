using Veyra.Domain.Observability;

namespace Veyra.Application.Common.Observability;

internal sealed class AlwaysOnRuntimeObservabilityState : IRuntimeObservabilityState
{
    public bool IsDiagnosticsEnabled => true;

    public bool IsLoggingEnabled => true;

    public event Action<RuntimeObservabilitySnapshot>? StateChanged
    {
        add { }
        remove { }
    }
}
