using System;

namespace Veyra.Desktop.Services.Scanning;

public sealed class RepositoryScanStatusChangedEventArgs(RepositoryScanStatusSnapshot snapshot)
    : EventArgs
{
    public RepositoryScanStatusSnapshot Snapshot { get; } = snapshot;
}
