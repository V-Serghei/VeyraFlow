using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Maintenance;

public interface IRepositoryRetentionDefaultsApplier
{
    Task<int> ApplyToRepositoryAsync(int repositoryId, CancellationToken ct = default);

    Task<int> ApplyToRepositoriesAsync(IEnumerable<int> repositoryIds, CancellationToken ct = default);
}
