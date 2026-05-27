using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Observability;

public interface IRuntimeObservabilitySettingsStore
{
    RuntimeObservabilityUserSettings? Load();

    Task<RuntimeObservabilityUserSettings?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(RuntimeObservabilityUserSettings settings, CancellationToken ct = default);
}
