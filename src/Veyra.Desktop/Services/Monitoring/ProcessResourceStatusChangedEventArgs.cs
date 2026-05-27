using System;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class ProcessResourceStatusChangedEventArgs(ProcessResourceSnapshotDto? snapshot) : EventArgs
{
    public ProcessResourceSnapshotDto? Snapshot { get; } = snapshot;
}
