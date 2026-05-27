using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryCardViewModel : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private DateTime? _lastActivityUtc;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusColor = "#9E9E9E";
    [ObservableProperty] private string _lastActivity = string.Empty;

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _versionCount;
    [ObservableProperty] private long _totalSizeBytes;
    [ObservableProperty] private string _sizeDisplay = "0 B";
    [ObservableProperty] private string _cloudSyncStatus = string.Empty;
    [ObservableProperty] private string _cloudQueueSummary = string.Empty;
    [ObservableProperty] private bool _showCloudSection = true;
    [ObservableProperty] private string _cloudModeText = string.Empty;
    [ObservableProperty] private string _cloudModeColor = "#9E9E9E";
    [ObservableProperty] private string _cloudModeBorderColor = "#2A3442";
    [ObservableProperty] private string _cloudModeBackgroundColor = "#0F1319";
    [ObservableProperty] private bool _showLiveSyncSection = true;
    [ObservableProperty] private string _liveSyncStateText = string.Empty;
    [ObservableProperty] private string _liveSyncModeText = string.Empty;
    [ObservableProperty] private string _liveSyncSummaryText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveSyncDetail))]
    private string _liveSyncDetailText = string.Empty;
    [ObservableProperty] private string _liveSyncAccentColor = "#94A3B8";
    [ObservableProperty] private string _liveSyncBorderColor = "#2A3442";
    [ObservableProperty] private string _liveSyncBackgroundColor = "#0F1319";

    [ObservableProperty] private string _cloudSyncStateKey = "idle";
    [ObservableProperty] private bool _isDirectoryAvailable = true;
    [ObservableProperty] private int _queuePendingCount;
    [ObservableProperty] private int _queueRunningCount;
    [ObservableProperty] private int _queueRetryCount;
    [ObservableProperty] private int _queueConflictCount;
    [ObservableProperty] private int _queueDeadLetterCount;
    [ObservableProperty] private int _queueFailedCount;
    [ObservableProperty] private int _queueCompletedCount;

    public ObservableCollection<string> LinkedFormats { get; } = new();

    public string FormatsDisplay => LinkedFormats.Count == 0
        ? Loc.T("dashboard.no_formats")
        : string.Join("  ", LinkedFormats);

    public string FormatsBadge => LinkedFormats.Count == 0
        ? Loc.T("dashboard.formats_badge.none")
        : Loc.P("dashboard.formats_badge", LinkedFormats.Count, LinkedFormats.Count);

    public string ShortPath
    {
        get
        {
            if (string.IsNullOrEmpty(DirectoryPath))
                return string.Empty;

            var parts = DirectoryPath.Replace('/', '\\').Split('\\');
            return parts.Length <= 2
                ? DirectoryPath
                : "...\\" + string.Join("\\", parts.Skip(parts.Length - 2));
        }
    }

    public bool ShowUnavailableHint => !IsDirectoryAvailable;
    public bool HasLiveSyncDetail => !string.IsNullOrWhiteSpace(LiveSyncDetailText);

    partial void OnIsDirectoryAvailableChanged(bool value)
        => OnPropertyChanged(nameof(ShowUnavailableHint));

    public void RefreshFormatsDisplay()
    {
        OnPropertyChanged(nameof(FormatsDisplay));
        OnPropertyChanged(nameof(FormatsBadge));
        OnPropertyChanged(nameof(ShortPath));
        OnPropertyChanged(nameof(ShowUnavailableHint));
        OnPropertyChanged(nameof(HasLiveSyncDetail));
    }

    public void RefreshLocalization()
    {
        RefreshFormatsDisplay();
    }
}
