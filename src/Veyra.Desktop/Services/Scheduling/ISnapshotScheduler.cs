using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Scheduling;

public interface ISnapshotScheduler
{
    void Start();
    Task StopAsync();
}
