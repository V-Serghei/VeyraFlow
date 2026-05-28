using System;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Monitoring;

public interface IMonitoringControlService
{
    bool IsEnabled { get; }

    event Action<bool>? StateChanged;

    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
