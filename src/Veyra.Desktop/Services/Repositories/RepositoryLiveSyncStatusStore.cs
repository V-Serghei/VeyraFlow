using System;
using System.Collections.Generic;
using System.Linq;

namespace Veyra.Desktop.Services.Repositories;

public sealed class RepositoryLiveSyncStatusStore : IRepositoryLiveSyncStatusStore
{
    private readonly object _gate = new();
    private readonly Dictionary<int, RepositoryLiveSyncStatusSnapshot> _statuses = [];

    public event EventHandler<RepositoryLiveSyncStatusChangedEventArgs>? StatusChanged;

    public RepositoryLiveSyncStatusSnapshot? Get(int repositoryId)
    {
        lock (_gate)
            return _statuses.GetValueOrDefault(repositoryId);
    }

    public IReadOnlyList<RepositoryLiveSyncStatusSnapshot> GetAll()
    {
        lock (_gate)
            return _statuses.Values.ToArray();
    }

    public void Update(RepositoryLiveSyncStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_statuses.TryGetValue(snapshot.RepositoryId, out var existing) && Equals(existing, snapshot))
                return;

            _statuses[snapshot.RepositoryId] = snapshot;
        }

        StatusChanged?.Invoke(this, new RepositoryLiveSyncStatusChangedEventArgs(snapshot.RepositoryId, snapshot));
    }

    public void Clear(int repositoryId)
    {
        var removed = false;
        lock (_gate)
            removed = _statuses.Remove(repositoryId);

        if (removed)
            StatusChanged?.Invoke(this, new RepositoryLiveSyncStatusChangedEventArgs(repositoryId, null));
    }
}
