using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Explorer;

public sealed class RepositorySnapshotHistoryEntryViewModel : ObservableObject
{
    public long SnapshotId { get; init; }
    public string? Title { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Kind { get; init; } = RepositorySnapshotKind.Manual;
    public bool IsArchived { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public int ChangedFilesCount { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"{Loc.T("snapshot.default_name_prefix")}_{CreatedAtUtc:yyyyMMdd_HHmmss}" : Title;
    public string DisplayTime => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string KindLabel => Kind switch
    {
        RepositorySnapshotKind.Automatic => Loc.T("filter.option.automatic"),
        RepositorySnapshotKind.Working => Loc.T("filter.option.working"),
        _ => Loc.T("filter.option.manual")
    };
    public string ArchivedLabel => IsArchived ? Loc.T("explorer.snapshot.archived") : string.Empty;
    public string ChangedFilesLabel => Loc.P("explorer.changed_files", ChangedFilesCount, ChangedFilesCount);
    public bool HasTags => Tags.Count > 0;
    public string TagsLabel => string.Join(" ", Tags.Select(tag => "#" + tag));

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(ArchivedLabel));
        OnPropertyChanged(nameof(ChangedFilesLabel));
        OnPropertyChanged(nameof(TagsLabel));
        OnPropertyChanged(nameof(HasTags));
    }
}
