namespace Veyra.Desktop.Services.Sync;

public sealed record RepositoryFsEventLeaseItem(
    long Id,
    string FullPath,
    string EventKind);
