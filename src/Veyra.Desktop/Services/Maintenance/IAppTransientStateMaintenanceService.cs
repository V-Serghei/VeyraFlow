using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Maintenance;

public interface IAppTransientStateMaintenanceService
{
    Task<AppTransientStateCleanupResult> ClearAsync(CancellationToken ct = default);
}
