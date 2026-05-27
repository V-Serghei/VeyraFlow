using System.Collections.Generic;

namespace Veyra.Desktop.Services.Monitoring.Models;

public sealed record OperationMonitorSnapshotDto(
    global::System.DateTime GeneratedAtUtc,
    OperationMonitorProcessSnapshotDto Process,
    IReadOnlyList<OperationMonitorRepositorySnapshotDto> ActiveRepositories,
    IReadOnlyList<OperationMonitorJournalSnapshotDto> RecentOperations);
