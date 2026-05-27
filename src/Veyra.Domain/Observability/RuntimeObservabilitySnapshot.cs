namespace Veyra.Domain.Observability;

public sealed record RuntimeObservabilitySnapshot(
    bool IsDiagnosticsEnabled,
    bool IsLoggingEnabled);
