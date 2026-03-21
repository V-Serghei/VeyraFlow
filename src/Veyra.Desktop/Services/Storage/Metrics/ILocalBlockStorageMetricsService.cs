using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Storage.Models;

namespace Veyra.Desktop.Services.Storage;

public interface ILocalBlockStorageMetricsService
{
    Task<LocalBlockStorageMetricsDto> GetMetricsAsync(CancellationToken ct = default);
}
