using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Maintenance;

public sealed class AppTransientStateMaintenanceService : IAppTransientStateMaintenanceService
{
    public Task<AppTransientStateCleanupResult> ClearAsync(CancellationToken ct = default)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "state");

        var deletedFiles = 0;
        var deletedDirectories = 0;

        if (!Directory.Exists(root))
            return Task.FromResult(new AppTransientStateCleanupResult(0, 0, root));

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(file);
            deletedFiles++;
        }

        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(path => path.Length))
        {
            ct.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: true);
            deletedDirectories++;
        }

        return Task.FromResult(new AppTransientStateCleanupResult(deletedFiles, deletedDirectories, root));
    }
}
