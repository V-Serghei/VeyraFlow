namespace Veyra.Domain.Observability;

public interface IRuntimeObservabilityState
{
    bool IsDiagnosticsEnabled { get; }

    bool IsLoggingEnabled { get; }

    event Action<RuntimeObservabilitySnapshot>? StateChanged;
}
