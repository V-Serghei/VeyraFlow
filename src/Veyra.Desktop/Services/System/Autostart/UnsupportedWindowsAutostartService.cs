using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.System;

public sealed class UnsupportedWindowsAutostartService : IWindowsAutostartService
{
    public bool IsSupported => false;
    public string EntryName => "VeyraFlow";

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string?> GetRegisteredCommandAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task EnableAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DisableAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
