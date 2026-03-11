using System;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositorySnapshotHistoryEntryViewModel
{
    public long SnapshotId { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public int ChangedFilesCount { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"snimok_{CreatedAtUtc:yyyyMMdd_HHmmss}" : Title;
    public string DisplayTime => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ChangedFilesLabel => Loc.P("explorer.changed_files", ChangedFilesCount, ChangedFilesCount);
}
