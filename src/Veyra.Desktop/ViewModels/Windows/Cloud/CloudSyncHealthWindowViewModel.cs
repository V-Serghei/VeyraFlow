using System;
using System.Collections.Specialized;
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

    public CloudSyncHealthWindowViewModel(AppSettingsViewModel source)
    {
        _source = source;
        _source.PropertyChanged += OnSourcePropertyChanged;
        _source.RepositorySyncIssues.CollectionChanged += OnRepositorySyncIssuesChanged;
    }

    public event Action? RequestClose;

    public AppSettingsViewModel Source => _source;
    public bool HasIssues => _source.HasRepositorySyncIssues;
    public int IssueCount => _source.RepositorySyncIssueCount;
    public int ConflictIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "conflict");
    public int AuthIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "auth");
    public int RetryIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "retry");
    public int StalledIssuesCount => _source.RepositorySyncIssues.Count(issue => issue.IssueCategoryCode == "stalled");
    public string SummaryText => Loc.F("app_settings.sync_health_center_summary", IssueCount, ConflictIssuesCount, AuthIssuesCount, RetryIssuesCount, StalledIssuesCount);

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

    private void OnRepositorySyncIssuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RaiseSummaryStateChanged();

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
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Detach()
    {
        _source.PropertyChanged -= OnSourcePropertyChanged;
        _source.RepositorySyncIssues.CollectionChanged -= OnRepositorySyncIssuesChanged;
    }
}
