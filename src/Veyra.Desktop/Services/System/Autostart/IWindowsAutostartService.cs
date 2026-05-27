using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.System;

public interface IWindowsAutostartService
{
    bool IsSupported { get; }
    string EntryName { get; }

    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task<string?> GetRegisteredCommandAsync(CancellationToken ct = default);
    Task EnableAsync(CancellationToken ct = default);
    Task DisableAsync(CancellationToken ct = default);
}
