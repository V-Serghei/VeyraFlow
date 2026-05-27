using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.State;

public interface IGlobalSearchFilterStore
{
    Task<IReadOnlyList<GlobalSearchFilterPreset>> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(IReadOnlyList<GlobalSearchFilterPreset> presets, CancellationToken ct = default);
}
