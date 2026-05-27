using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public interface IOperationMonitorService
{
    Task<OperationMonitorSnapshotDto> CaptureAsync(CancellationToken cancellationToken = default);
}
