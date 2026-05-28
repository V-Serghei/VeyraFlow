using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class TrayPanelWindowViewModel : ObservableObject
{
    private bool _suppressSelectedRepositoryChanged;
    private Func<Task>? _openAppAction;
    private Func<Task>? _openDashboardAction;
    private Func<Task>? _openSearchAction;
    private Func<Task>? _openSettingsAction;
    private Func<Task>? _openSyncHealthCenterAction;
    private Func<int, Task>? _openRepositorySettingsAction;
    private Func<int, Task>? _retryIssueAction;
    private Func<int, Task>? _cancelIssueAction;
    private Func<int?, Task>? _createSnapshotAction;
    private Func<Task>? _toggleCloudPauseAction;
    private Func<Task>? _processCloudQueueAction;
    private Func<Task>? _pushAllToCloudAction;
    private Func<Task>? _exitAction;
    public event Action? RequestClose;
    public event Action<int?>? SelectedRepositoryChanged;

    public ObservableCollection<TrayPanelRepositoryOptionViewModel> Repositories { get; } = [];
    public ObservableCollection<TrayPanelRecentActionViewModel> RecentActions { get; } = [];
    public ObservableCollection<TrayPanelIssueItemViewModel> IssueItems { get; } = [];

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _connectivityText = string.Empty;
    [ObservableProperty] private string _connectivityAccentColor = "#6EA8FF";
    [ObservableProperty] private string _connectivityBackgroundColor = "#1A6EA8FF";
    [ObservableProperty] private string _cloudHintText = string.Empty;
    [ObservableProperty] private bool _showCloudHint;
    [ObservableProperty] private string _cloudPauseButtonText = string.Empty;
    [ObservableProperty] private bool _canRunCloudActions;
    [ObservableProperty] private bool _hasCloudAccess;
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private string _currentActivityText = string.Empty;
    [ObservableProperty] private string _currentActivityAccentColor = "#6EA8FF";
    [ObservableProperty] private string _currentActivityBackgroundColor = "#1A6EA8FF";
    [ObservableProperty] private bool _showCurrentActivity;
    [ObservableProperty] private string _processLoadText = string.Empty;
    [ObservableProperty] private string _processLoadDetailText = string.Empty;
    [ObservableProperty] private string _processLoadPeakText = string.Empty;
    [ObservableProperty] private string _processLoadHistoryText = string.Empty;
    [ObservableProperty] private string _processLoadAccentColor = "#6EA8FF";
    [ObservableProperty] private string _processLoadBackgroundColor = "#1A6EA8FF";
    [ObservableProperty] private bool _showProcessLoad;
    [ObservableProperty] private string _selectedRepositoryName = string.Empty;
    [ObservableProperty] private string _selectedRepositoryPath = string.Empty;
    [ObservableProperty] private string _selectedRepositoryMetricsText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryLastSnapshotText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryLastSnapshotGlyph = "\uE823";
    [ObservableProperty] private string _selectedRepositoryLastSnapshotAccentColor = "#6EA8FF";
    [ObservableProperty] private string _selectedRepositoryLiveSyncText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryLiveSyncDetailText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryLiveSyncGlyph = "\uE895";
    [ObservableProperty] private string _selectedRepositoryLiveSyncAccentColor = "#6EA8FF";
    [ObservableProperty] private string _selectedRepositoryCloudSyncText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryCloudSyncGlyph = "\uE753";
    [ObservableProperty] private string _selectedRepositoryCloudSyncAccentColor = "#6EA8FF";
    [ObservableProperty] private string _selectedRepositoryQueueText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryQueueGlyph = "\uE895";
    [ObservableProperty] private string _selectedRepositoryQueueAccentColor = "#6EA8FF";
    [ObservableProperty] private double _selectedRepositoryProgressValue;
    [ObservableProperty] private double _selectedRepositoryProgressMaximum = 1d;
    [ObservableProperty] private string _selectedRepositoryProgressText = string.Empty;
    [ObservableProperty] private string _selectedRepositoryProgressAccentColor = "#6EA8FF";
    [ObservableProperty] private bool _showSelectedRepositoryProgress;
    [ObservableProperty] private bool _hasSelectedRepository;
    [ObservableProperty] private TrayPanelRepositoryOptionViewModel? _selectedRepository;

    public bool HasRepositories => Repositories.Count > 0;
    public bool HasRecentActions => RecentActions.Count > 0;
    public bool HasIssueItems => IssueItems.Count > 0;
    public bool HasSelectedRepositoryLiveSyncDetail => !string.IsNullOrWhiteSpace(SelectedRepositoryLiveSyncDetailText);

    public void ConfigureActions(
        Func<Task> openAppAction,
        Func<Task> openDashboardAction,
        Func<Task> openSearchAction,
        Func<Task> openSettingsAction,
        Func<Task> openSyncHealthCenterAction,
        Func<int, Task> openRepositorySettingsAction,
        Func<int, Task> retryIssueAction,
        Func<int, Task> cancelIssueAction,
        Func<int?, Task> createSnapshotAction,
        Func<Task> toggleCloudPauseAction,
        Func<Task> processCloudQueueAction,
        Func<Task> pushAllToCloudAction,
        Func<Task> exitAction)
    {
        _openAppAction = openAppAction;
        _openDashboardAction = openDashboardAction;
        _openSearchAction = openSearchAction;
        _openSettingsAction = openSettingsAction;
        _openSyncHealthCenterAction = openSyncHealthCenterAction;
        _openRepositorySettingsAction = openRepositorySettingsAction;
        _retryIssueAction = retryIssueAction;
        _cancelIssueAction = cancelIssueAction;
        _createSnapshotAction = createSnapshotAction;
        _toggleCloudPauseAction = toggleCloudPauseAction;
        _processCloudQueueAction = processCloudQueueAction;
        _pushAllToCloudAction = pushAllToCloudAction;
        _exitAction = exitAction;
    }

    public void ApplyState(
        string statusText,
        string connectivityText,
        string connectivityAccentColor,
        string connectivityBackgroundColor,
        string cloudPauseButtonText,
        bool hasCloudAccess,
        bool canRunCloudActions,
        string? cloudHintText,
        IReadOnlyList<TrayPanelRepositoryOptionViewModel> repositories,
        int? selectedRepositoryId,
        string lastUpdatedText,
        string? currentActivityText,
        string currentActivityAccentColor,
        string currentActivityBackgroundColor,
        string? processLoadText,
        string? processLoadDetailText,
        string? processLoadPeakText,
        string? processLoadHistoryText,
        string processLoadAccentColor,
        string processLoadBackgroundColor,
        TrayPanelRepositoryState? selectedRepositoryState,
        IReadOnlyList<TrayPanelRecentActionViewModel> recentActions,
        IReadOnlyList<TrayPanelIssueItemViewModel> issueItems)
    {
        var previousSuppression = _suppressSelectedRepositoryChanged;
        _suppressSelectedRepositoryChanged = true;

        try
        {
            StatusText = statusText;
            ConnectivityText = connectivityText;
            ConnectivityAccentColor = connectivityAccentColor;
            ConnectivityBackgroundColor = connectivityBackgroundColor;
            CloudPauseButtonText = cloudPauseButtonText;
            HasCloudAccess = hasCloudAccess;
            CanRunCloudActions = canRunCloudActions;
            CloudHintText = cloudHintText ?? string.Empty;
            ShowCloudHint = !string.IsNullOrWhiteSpace(CloudHintText);
            LastUpdatedText = lastUpdatedText;
            CurrentActivityText = currentActivityText ?? string.Empty;
            CurrentActivityAccentColor = currentActivityAccentColor;
            CurrentActivityBackgroundColor = currentActivityBackgroundColor;
            ShowCurrentActivity = !string.IsNullOrWhiteSpace(CurrentActivityText);
            ProcessLoadText = processLoadText ?? string.Empty;
            ProcessLoadDetailText = processLoadDetailText ?? string.Empty;
            ProcessLoadPeakText = processLoadPeakText ?? string.Empty;
            ProcessLoadHistoryText = processLoadHistoryText ?? string.Empty;
            ProcessLoadAccentColor = processLoadAccentColor;
            ProcessLoadBackgroundColor = processLoadBackgroundColor;
            ShowProcessLoad = !string.IsNullOrWhiteSpace(ProcessLoadText);

            Repositories.Clear();
            foreach (var repository in repositories)
                Repositories.Add(repository);

            RecentActions.Clear();
            foreach (var action in recentActions)
                RecentActions.Add(action);

            IssueItems.Clear();
            foreach (var issue in issueItems)
                IssueItems.Add(issue);

            SelectedRepository = Repositories.FirstOrDefault(r => r.Id == selectedRepositoryId)
                                 ?? Repositories.FirstOrDefault();

            ApplySelectedRepositoryState(selectedRepositoryState);

            OnPropertyChanged(nameof(HasRepositories));
            OnPropertyChanged(nameof(HasRecentActions));
            OnPropertyChanged(nameof(HasIssueItems));
        }
        finally
        {
            _suppressSelectedRepositoryChanged = previousSuppression;
        }
    }

    [RelayCommand]
    private async Task OpenAppAsync()
    {
        await ExecuteAndCloseAsync(_openAppAction);
    }

    [RelayCommand]
    private async Task OpenDashboardAsync()
    {
        await ExecuteAndCloseAsync(_openDashboardAction);
    }

    [RelayCommand]
    private async Task OpenSearchAsync()
    {
        await ExecuteAndCloseAsync(_openSearchAction);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await ExecuteAndCloseAsync(_openSettingsAction);
    }

    [RelayCommand]
    private async Task OpenSyncHealthCenterAsync()
    {
        await ExecuteAndCloseAsync(_openSyncHealthCenterAction);
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        await ExecuteAndCloseAsync(_createSnapshotAction, SelectedRepository?.Id);
    }

    [RelayCommand]
    private async Task ToggleCloudPauseAsync()
    {
        await ExecuteAndCloseAsync(_toggleCloudPauseAction);
    }

    [RelayCommand]
    private async Task ProcessCloudQueueAsync()
    {
        await ExecuteAndCloseAsync(_processCloudQueueAction);
    }

    [RelayCommand]
    private async Task PushAllToCloudAsync()
    {
        await ExecuteAndCloseAsync(_pushAllToCloudAction);
    }

    [RelayCommand]
    private async Task OpenIssueRepositorySettingsAsync(TrayPanelIssueItemViewModel? issue)
    {
        if (issue is null || _openRepositorySettingsAction is null)
            return;

        RequestClose?.Invoke();
        await _openRepositorySettingsAction(issue.RepositoryId);
    }

    [RelayCommand]
    private async Task RetryIssueAsync(TrayPanelIssueItemViewModel? issue)
    {
        if (issue is null || _retryIssueAction is null)
            return;

        RequestClose?.Invoke();
        await _retryIssueAction(issue.RepositoryId);
    }

    [RelayCommand]
    private async Task CancelIssueAsync(TrayPanelIssueItemViewModel? issue)
    {
        if (issue is null || _cancelIssueAction is null)
            return;

        RequestClose?.Invoke();
        await _cancelIssueAction(issue.RepositoryId);
    }

    [RelayCommand]
    private async Task ExitAsync()
    {
        await ExecuteAndCloseAsync(_exitAction);
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    partial void OnSelectedRepositoryChanged(TrayPanelRepositoryOptionViewModel? value)
    {
        if (_suppressSelectedRepositoryChanged)
            return;

        SelectedRepositoryChanged?.Invoke(value?.Id);
    }

    private void ApplySelectedRepositoryState(TrayPanelRepositoryState? state)
    {
        if (state is null)
        {
            SelectedRepositoryName = string.Empty;
            SelectedRepositoryPath = string.Empty;
            SelectedRepositoryMetricsText = string.Empty;
            SelectedRepositoryLastSnapshotText = string.Empty;
            SelectedRepositoryLastSnapshotGlyph = "\uE823";
            SelectedRepositoryLastSnapshotAccentColor = "#6EA8FF";
            SelectedRepositoryLiveSyncText = string.Empty;
            SelectedRepositoryLiveSyncDetailText = string.Empty;
            SelectedRepositoryLiveSyncGlyph = "\uE895";
            SelectedRepositoryLiveSyncAccentColor = "#6EA8FF";
            SelectedRepositoryCloudSyncText = string.Empty;
            SelectedRepositoryCloudSyncGlyph = "\uE753";
            SelectedRepositoryCloudSyncAccentColor = "#6EA8FF";
            SelectedRepositoryQueueText = string.Empty;
            SelectedRepositoryQueueGlyph = "\uE895";
            SelectedRepositoryQueueAccentColor = "#6EA8FF";
            SelectedRepositoryProgressValue = 0d;
            SelectedRepositoryProgressMaximum = 1d;
            SelectedRepositoryProgressText = string.Empty;
            SelectedRepositoryProgressAccentColor = "#6EA8FF";
            ShowSelectedRepositoryProgress = false;
            HasSelectedRepository = false;
            OnPropertyChanged(nameof(HasSelectedRepositoryLiveSyncDetail));
            return;
        }

        SelectedRepositoryName = state.Name;
        SelectedRepositoryPath = state.Path;
        SelectedRepositoryMetricsText = state.MetricsText;
        SelectedRepositoryLastSnapshotText = state.LastSnapshotText;
        SelectedRepositoryLastSnapshotGlyph = state.LastSnapshotGlyph;
        SelectedRepositoryLastSnapshotAccentColor = state.LastSnapshotAccentColor;
        SelectedRepositoryLiveSyncText = state.LiveSyncText;
        SelectedRepositoryLiveSyncDetailText = state.LiveSyncDetailText;
        SelectedRepositoryLiveSyncGlyph = state.LiveSyncGlyph;
        SelectedRepositoryLiveSyncAccentColor = state.LiveSyncAccentColor;
        SelectedRepositoryCloudSyncText = state.CloudSyncText;
        SelectedRepositoryCloudSyncGlyph = state.CloudSyncGlyph;
        SelectedRepositoryCloudSyncAccentColor = state.CloudSyncAccentColor;
        SelectedRepositoryQueueText = state.QueueText;
        SelectedRepositoryQueueGlyph = state.QueueGlyph;
        SelectedRepositoryQueueAccentColor = state.QueueAccentColor;
        SelectedRepositoryProgressValue = state.ProgressValue;
        SelectedRepositoryProgressMaximum = state.ProgressMaximum <= 0d ? 1d : state.ProgressMaximum;
        SelectedRepositoryProgressText = state.ProgressText;
        SelectedRepositoryProgressAccentColor = state.ProgressAccentColor;
        ShowSelectedRepositoryProgress = state.ShowProgress;
        HasSelectedRepository = true;
        OnPropertyChanged(nameof(HasSelectedRepositoryLiveSyncDetail));
    }

    private async Task ExecuteAndCloseAsync(Func<Task>? action)
    {
        if (action is null)
            return;

        RequestClose?.Invoke();
        await action();
    }

    private async Task ExecuteAndCloseAsync(Func<int?, Task>? action, int? repositoryId)
    {
        if (action is null)
            return;

        RequestClose?.Invoke();
        await action(repositoryId);
    }
}
