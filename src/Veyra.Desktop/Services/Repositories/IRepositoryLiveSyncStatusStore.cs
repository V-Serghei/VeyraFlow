using System;
using System.Collections.Generic;

namespace Veyra.Desktop.Services.Repositories;

public interface IRepositoryLiveSyncStatusStore
{
    event EventHandler<RepositoryLiveSyncStatusChangedEventArgs>? StatusChanged;

    RepositoryLiveSyncStatusSnapshot? Get(int repositoryId);

    IReadOnlyList<RepositoryLiveSyncStatusSnapshot> GetAll();

    void Update(RepositoryLiveSyncStatusSnapshot snapshot);

    void Clear(int repositoryId);
}
