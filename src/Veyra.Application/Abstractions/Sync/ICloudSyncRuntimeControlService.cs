using System;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Sync;

public interface ICloudSyncRuntimeControlService
{
    bool IsPaused { get; }

    CloudSyncRuntimeSnapshot Snapshot { get; }

    CancellationToken PauseToken { get; }

    event Action<CloudSyncRuntimeSnapshot>? StateChanged;

    Task SetPausedAsync(bool paused, CancellationToken ct = default);
}
