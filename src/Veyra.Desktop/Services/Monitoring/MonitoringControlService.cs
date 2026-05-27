using System;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class MonitoringControlService : IMonitoringControlService
{
    private readonly IMonitoringSettingsStore _store;
    private readonly object _sync = new();
    private bool _isEnabled;

    public MonitoringControlService(IMonitoringSettingsStore store)
    {
        _store = store;
        _isEnabled = (store.Load() ?? MonitoringUserSettings.Default).Enabled;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
                return _isEnabled;
        }
    }

    public event Action<bool>? StateChanged;

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var shouldUpdate = false;

        lock (_sync)
        {
            shouldUpdate = _isEnabled != enabled;
        }

        if (!shouldUpdate)
            return;

        await _store.SaveAsync(new MonitoringUserSettings(enabled), ct);

        lock (_sync)
            _isEnabled = enabled;

        StateChanged?.Invoke(enabled);
    }
}
