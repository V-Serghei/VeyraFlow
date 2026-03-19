using System;

namespace Veyra.Desktop.Services.State;

public sealed class RepositoryDashboardFilterPreset
{
    public string Name { get; init; } = string.Empty;
    public string SearchQuery { get; init; } = string.Empty;
    public string AvailabilityFilter { get; init; } = "all";
    public string SyncStateFilter { get; init; } = "all";
    public string FormatTagFilter { get; init; } = "all";
    public string MinSizeMb { get; init; } = string.Empty;
    public string MaxSizeMb { get; init; } = string.Empty;
    public bool OnlyQueueIssues { get; init; }
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
}
