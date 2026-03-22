using System;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Sync.Runtime.Models;

namespace Veyra.Desktop.Services.Sync.Runtime;

public interface ICloudSyncRuntimeControlService
{
    bool IsPaused { get; }

    CloudSyncRuntimeSnapshot Snapshot { get; }

    CancellationToken PauseToken { get; }

    event Action<CloudSyncRuntimeSnapshot>? StateChanged;

    Task SetPausedAsync(bool paused, CancellationToken ct = default);
}
