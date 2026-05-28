using System;

namespace Veyra.Desktop.Services.State;

public sealed class GlobalSearchFilterPreset
{
    public string Name { get; init; } = string.Empty;
    public string SearchQuery { get; init; } = string.Empty;
    public int? RepositoryId { get; init; }
    public string TrackedFormatFilter { get; init; } = "all";
    public string EntryTypeFilter { get; init; } = "all";
    public string ExtensionFilter { get; init; } = "all";
    public string ModifiedWindowFilter { get; init; } = "all";
    public string SnapshotTagFilter { get; init; } = "all";
    public string MinSizeMb { get; init; } = string.Empty;
    public string MaxSizeMb { get; init; } = string.Empty;
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
}
