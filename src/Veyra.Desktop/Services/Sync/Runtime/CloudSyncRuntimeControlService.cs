using System;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Sync.Runtime.Models;

namespace Veyra.Desktop.Services.Sync.Runtime;

public sealed class CloudSyncRuntimeControlService : ICloudSyncRuntimeControlService
{
    private readonly ICloudSyncRuntimeSettingsStore _store;
    private readonly object _sync = new();
    private CancellationTokenSource _pauseCts;
    private bool _isPaused;

    public CloudSyncRuntimeControlService(ICloudSyncRuntimeSettingsStore store)
    {
        _store = store;

        var initial = store.Load() ?? CloudSyncRuntimeUserSettings.Default;
        _isPaused = initial.IsPaused;
        _pauseCts = CreatePauseTokenSource(_isPaused);
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _isPaused;
        }
    }

    public CloudSyncRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new CloudSyncRuntimeSnapshot(_isPaused);
        }
    }

    public CancellationToken PauseToken
    {
        get
        {
            lock (_sync)
                return _pauseCts.Token;
        }
    }

    public event Action<CloudSyncRuntimeSnapshot>? StateChanged;

    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        CloudSyncRuntimeSnapshot snapshot;
        CancellationTokenSource? obsoleteCts = null;

        lock (_sync)
        {
            if (_isPaused == paused)
                return;

            _isPaused = paused;
            obsoleteCts = _pauseCts;
            _pauseCts = CreatePauseTokenSource(paused);
            snapshot = new CloudSyncRuntimeSnapshot(_isPaused);
        }

        try
        {
            if (paused)
                obsoleteCts.Cancel();
        }
        catch
        {
        }
        finally
        {
            obsoleteCts.Dispose();
        }

        StateChanged?.Invoke(snapshot);
        await _store.SaveAsync(new CloudSyncRuntimeUserSettings(snapshot.IsPaused), ct);
    }

    private static CancellationTokenSource CreatePauseTokenSource(bool paused)
    {
        var cts = new CancellationTokenSource();
        if (paused)
            cts.Cancel();

        return cts;
    }
}
