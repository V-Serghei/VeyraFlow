using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Monitoring;

public interface IMonitoringSettingsStore
{
    MonitoringUserSettings? Load();

    Task<MonitoringUserSettings?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(MonitoringUserSettings settings, CancellationToken ct = default);
}
