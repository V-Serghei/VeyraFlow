using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.State;

public interface IRepositoryDashboardFilterStore
{
    Task<IReadOnlyList<RepositoryDashboardFilterPreset>> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(IReadOnlyList<RepositoryDashboardFilterPreset> presets, CancellationToken ct = default);
}
