using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryCardViewModel : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private DateTime? _lastActivityUtc;
    [ObservableProperty] private string _statusText = "Local";
    [ObservableProperty] private string _statusColor = "#9E9E9E";
    [ObservableProperty] private string _lastActivity = "Just now";

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _versionCount;
    [ObservableProperty] private long _totalSizeBytes;
    [ObservableProperty] private string _sizeDisplay = "0 MB";
    [ObservableProperty] private string _cloudSyncStatus = "idle";
    [ObservableProperty] private string _cloudQueueSummary = "pending 0 / running 0 / retry 0 / conflict 0 / dead-letter 0";

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
        ? "No formats"
        : string.Join("  ", LinkedFormats);

    public string FormatsBadge => LinkedFormats.Count == 0
        ? "0 formats"
        : $"{LinkedFormats.Count} format(s)";

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

    public void RefreshFormatsDisplay()
    {
        OnPropertyChanged(nameof(FormatsDisplay));
        OnPropertyChanged(nameof(FormatsBadge));
        OnPropertyChanged(nameof(ShortPath));
    }
}
