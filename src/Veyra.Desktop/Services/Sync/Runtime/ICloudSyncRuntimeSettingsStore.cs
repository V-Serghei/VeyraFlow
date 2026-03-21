using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Sync.Runtime.Models;

namespace Veyra.Desktop.Services.Sync.Runtime;

public interface ICloudSyncRuntimeSettingsStore
{
    CloudSyncRuntimeUserSettings? Load();

    Task<CloudSyncRuntimeUserSettings?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CloudSyncRuntimeUserSettings settings, CancellationToken ct = default);
}
