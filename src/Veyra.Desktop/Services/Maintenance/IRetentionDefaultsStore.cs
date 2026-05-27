using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Maintenance;

public interface IRetentionDefaultsStore
{
    RetentionDefaultsUserSettings? Load();
    Task<RetentionDefaultsUserSettings?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(RetentionDefaultsUserSettings settings, CancellationToken ct = default);
}
