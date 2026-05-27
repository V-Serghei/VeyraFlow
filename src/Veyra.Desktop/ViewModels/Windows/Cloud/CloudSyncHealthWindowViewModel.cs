using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Localization;
using Veyra.Desktop.ViewModels.Pages.Settings;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class CloudSyncHealthWindowViewModel : ObservableObject
{
    private readonly AppSettingsViewModel _source;
    private const string AllFilterKey = "all";

    public CloudSyncHealthWindowViewModel(AppSettingsViewModel source)
    {
        _source = source;
        _source.PropertyChanged += OnSourcePropertyChanged;
        _source.RepositorySyncIssues.CollectionChanged += OnRepositorySyncIssuesChanged;
        RebuildFilters();
        RefreshFilteredIssues();
    }

    public event Action? RequestClose;

    public AppSettingsViewModel Source => _source;
    public ObservableCollection<CloudSyncHealthFilterOptionViewModel> Filters { get; } = [];
    public ObservableCollection<AppRepositorySyncIssueItemViewModel> FilteredIssues { get; } = [];
    public bool HasIssues => _source.HasRepositorySyncIssues;
    public bool HasFilteredIssues => FilteredIssues.Count > 0;
    public int IssueCount => _source.RepositorySyncIssueCount;
    public int ConflictIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "conflict");
    public int AuthIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "auth");
    public int RetryIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "retry");
    public int StalledIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "stalled");
    public int FailedIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "failed");
    public int QueueIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "queue");
    public string SummaryText => Loc.F("app_settings.sync_health_center_summary", IssueCount, ConflictIssuesCount, AuthIssuesCount, RetryIssuesCount, StalledIssuesCount);
    public string EmptyStateText => HasIssues
        ? Loc.T("app_settings.sync_health_center_filter_empty")
        : Loc.T("app_settings.repository_sync_health_empty");

    [ObservableProperty] private CloudSyncHealthFilterOptionViewModel? _selectedFilter;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _source.RefreshSyncHealthAsync();
        RaiseSummaryStateChanged();
    }

    [RelayCommand]
    private void Close()
    {
        Detach();
        RequestClose?.Invoke();
    }

    partial void OnSelectedFilterChanged(CloudSyncHealthFilterOptionViewModel? value)
        => RefreshFilteredIssues();

    private void OnRepositorySyncIssuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFilters();
        RefreshFilteredIssues();
        RaiseSummaryStateChanged();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettingsViewModel.RepositorySyncIssueCount)
            or nameof(AppSettingsViewModel.IsSyncBusy))
        {
            RaiseSummaryStateChanged();
        }
    }

    private void RaiseSummaryStateChanged()
    {
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(ConflictIssuesCount));
        OnPropertyChanged(nameof(AuthIssuesCount));
        OnPropertyChanged(nameof(RetryIssuesCount));
        OnPropertyChanged(nameof(StalledIssuesCount));
        OnPropertyChanged(nameof(FailedIssuesCount));
        OnPropertyChanged(nameof(QueueIssuesCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasFilteredIssues));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void RebuildFilters()
    {
        var selectedKey = SelectedFilter?.Key ?? AllFilterKey;
        var items = new[]
        {
            new CloudSyncHealthFilterOptionViewModel(AllFilterKey, Loc.T("app_settings.sync_health_filter_all")),
            new CloudSyncHealthFilterOptionViewModel("conflict", Loc.T("app_settings.sync_health_filter_conflict")),
            new CloudSyncHealthFilterOptionViewModel("auth", Loc.T("app_settings.sync_health_filter_auth")),
            new CloudSyncHealthFilterOptionViewModel("retry", Loc.T("app_settings.sync_health_filter_retry")),
            new CloudSyncHealthFilterOptionViewModel("stalled", Loc.T("app_settings.sync_health_filter_stalled")),
            new CloudSyncHealthFilterOptionViewModel("failed", Loc.T("app_settings.sync_health_filter_failed")),
            new CloudSyncHealthFilterOptionViewModel("queue", Loc.T("app_settings.sync_health_filter_queue"))
        };

        Filters.Clear();
        foreach (var item in items)
            Filters.Add(item);

        SelectedFilter = Filters.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                         ?? Filters.FirstOrDefault();
    }

    private void RefreshFilteredIssues()
    {
        var filterKey = SelectedFilter?.Key ?? AllFilterKey;
        var items = _source.RepositorySyncIssues
            .Where(issue => string.Equals(filterKey, AllFilterKey, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(issue.IssueCategoryCode, filterKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        FilteredIssues.Clear();
        foreach (var issue in items)
            FilteredIssues.Add(issue);

        OnPropertyChanged(nameof(HasFilteredIssues));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    public void Detach()
    {
        _source.PropertyChanged -= OnSourcePropertyChanged;
        _source.RepositorySyncIssues.CollectionChanged -= OnRepositorySyncIssuesChanged;
    }
}
