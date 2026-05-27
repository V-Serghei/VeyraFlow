using System;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Domain.Observability;

namespace Veyra.Desktop.Services.Observability;

public sealed class RuntimeObservabilityControlService : IRuntimeObservabilityControlService
{
    private readonly IRuntimeObservabilitySettingsStore _store;
    private readonly object _sync = new();

    private bool _diagnosticsEnabled;
    private bool _loggingEnabled;

    public RuntimeObservabilityControlService(IRuntimeObservabilitySettingsStore store)
    {
        _store = store;

        var initial = store.Load() ?? RuntimeObservabilityUserSettings.Default;
        _diagnosticsEnabled = initial.DiagnosticsEnabled;
        _loggingEnabled = initial.LoggingEnabled;
    }

    public bool IsDiagnosticsEnabled
    {
        get
        {
            lock (_sync)
                return _diagnosticsEnabled;
        }
    }

    public bool IsLoggingEnabled
    {
        get
        {
            lock (_sync)
                return _loggingEnabled;
        }
    }

    public RuntimeObservabilitySnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new RuntimeObservabilitySnapshot(_diagnosticsEnabled, _loggingEnabled);
        }
    }

    public event Action<RuntimeObservabilitySnapshot>? StateChanged;

    public Task SetDiagnosticsEnabledAsync(bool enabled, CancellationToken ct = default)
        => UpdateAsync(diagnosticsEnabled: enabled, loggingEnabled: null, ct);

    public Task SetLoggingEnabledAsync(bool enabled, CancellationToken ct = default)
        => UpdateAsync(diagnosticsEnabled: null, loggingEnabled: enabled, ct);

    private async Task UpdateAsync(bool? diagnosticsEnabled, bool? loggingEnabled, CancellationToken ct)
    {
        RuntimeObservabilitySnapshot snapshot;

        lock (_sync)
        {
            var nextDiagnostics = diagnosticsEnabled ?? _diagnosticsEnabled;
            var nextLogging = loggingEnabled ?? _loggingEnabled;

            if (nextDiagnostics == _diagnosticsEnabled && nextLogging == _loggingEnabled)
                return;

            _diagnosticsEnabled = nextDiagnostics;
            _loggingEnabled = nextLogging;
            snapshot = new RuntimeObservabilitySnapshot(_diagnosticsEnabled, _loggingEnabled);
        }

        StateChanged?.Invoke(snapshot);
        await _store.SaveAsync(new RuntimeObservabilityUserSettings(snapshot.IsDiagnosticsEnabled, snapshot.IsLoggingEnabled), ct);
    }
}
