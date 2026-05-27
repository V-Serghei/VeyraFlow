using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.State;

public interface IRepositoryExplorerFilterStore
{
    Task<IReadOnlyList<RepositoryExplorerFilterPreset>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<RepositoryExplorerFilterPreset> presets, CancellationToken ct = default);
}
