using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public interface IProcessResourceMonitorService
{
    ProcessResourceSnapshotDto Capture();
}
