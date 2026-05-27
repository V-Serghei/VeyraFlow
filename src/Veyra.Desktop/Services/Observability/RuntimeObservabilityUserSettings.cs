namespace Veyra.Desktop.Services.Observability;

public sealed record RuntimeObservabilityUserSettings(
    bool DiagnosticsEnabled,
    bool LoggingEnabled)
{
    public static RuntimeObservabilityUserSettings Default { get; } = new(
        DiagnosticsEnabled: true,
        LoggingEnabled: true);
}
