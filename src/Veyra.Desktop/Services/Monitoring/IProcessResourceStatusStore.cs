using System;
using System.Collections.Generic;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public interface IProcessResourceStatusStore
{
    event EventHandler<ProcessResourceStatusChangedEventArgs>? StatusChanged;

    ProcessResourceSnapshotDto? Snapshot { get; }

    IReadOnlyList<ProcessResourceHistoryEntryDto> History { get; }
}
