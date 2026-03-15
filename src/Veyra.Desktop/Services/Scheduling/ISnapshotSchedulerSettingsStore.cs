using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Scheduling;

public interface ISnapshotSchedulerSettingsStore
{
    Task<SnapshotSchedulerUserSettings?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SnapshotSchedulerUserSettings settings, CancellationToken ct = default);
}
