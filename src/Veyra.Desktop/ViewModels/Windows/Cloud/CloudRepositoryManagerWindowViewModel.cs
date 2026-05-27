using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class CloudRepositoryManagerWindowViewModel(
    ICloudRepositoryManagementService cloudManager,
    IWindowService windows) : ObservableObject
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    [ObservableProperty] private string _accountName = Loc.T("cloud_manager.not_signed_in");
    [ObservableProperty] private string _accountEmail = string.Empty;
    [ObservableProperty] private bool _hasAccountEmail;
    [ObservableProperty] private string _connectionText = Loc.T("cloud_manager.unknown");
    [ObservableProperty] private string _storageText = "0 B";
    [ObservableProperty] private string _summaryText = Loc.T("cloud_manager.loading");
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private CloudRepositoryManagerRepositoryItemViewModel? _selectedRepository;

    public ObservableCollection<CloudRepositoryManagerRepositoryItemViewModel> Repositories { get; } = [];
    public ObservableCollection<CloudRepositoryOperationItemViewModel> Operations { get; } = [];

    public event Action? RequestClose;

    public void StartLiveRefresh()
    {
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    public void StopLiveRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (IsBusy)
            return;

        await RefreshCoreAsync(isAutomatic: true);
    }

    [RelayCommand]
    public async Task RefreshAsync()
        => await RefreshCoreAsync(isAutomatic: false);

    private async Task RefreshCoreAsync(bool isAutomatic)
    {
        if (cloudManager is null)
            return;

        if (!isAutomatic)
        {
            IsBusy = true;
            StatusText = Loc.T("cloud_manager.refreshing");
        }
        try
        {
            var overview = await cloudManager.GetOverviewAsync();
            AccountEmail = overview.AccountEmail ?? string.Empty;
            HasAccountEmail = !string.IsNullOrWhiteSpace(AccountEmail);
            AccountName = ResolveAccountName(overview.AccountDisplayName, overview.AccountEmail);
            ConnectionText = LocalizeToken("cloud_manager.connection", overview.ConnectionState);
            StorageText = FormatBytes(overview.CloudStorageUsedBytes);
            SummaryText = Loc.F(
                "cloud_manager.summary",
                overview.CloudRepositoryCount,
                overview.PendingOperationCount,
                overview.FailedOperationCount);

            Repositories.Clear();
            foreach (var repo in overview.Repositories)
                Repositories.Add(new CloudRepositoryManagerRepositoryItemViewModel(repo));

            Operations.Clear();
            foreach (var operation in overview.Operations)
                Operations.Add(new CloudRepositoryOperationItemViewModel(operation));

            StatusText = Loc.T("cloud_manager.up_to_date");
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("cloud_manager.refresh_failed", ex.Message);
        }
        finally
        {
            if (!isAutomatic)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenRestoreWizardAsync(CloudRepositoryManagerRepositoryItemViewModel? repository)
    {
        if (repository is null || windows is null)
            return;

        var owner = windows.GetActiveWindow();
        var wizard = windows.Create<CloudRestoreWizardWindow>();
        if (wizard.DataContext is CloudRestoreWizardWindowViewModel vm)
            await vm.InitializeAsync(repository.CloudRepositoryId, repository.Name, repository.CurrentLocalPath);

        if (owner is null)
            windows.Show(wizard);
        else
            await windows.ShowDialogAsync(wizard, owner);

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SyncNowAsync(CloudRepositoryManagerRepositoryItemViewModel? repository)
    {
        if (repository is null || cloudManager is null)
            return;

        await cloudManager.QueueSyncNowAsync(repository.CloudRepositoryId);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CompareAsync(CloudRepositoryManagerRepositoryItemViewModel? repository)
    {
        if (repository is null || cloudManager is null)
            return;

        await cloudManager.QueueCompareWithLocalAsync(repository.CloudRepositoryId, repository.CurrentLocalPath);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteFromCloudAsync(CloudRepositoryManagerRepositoryItemViewModel? repository)
    {
        if (repository is null || cloudManager is null)
            return;

        if (!await ConfirmCloudDeleteAsync(repository))
            return;

        await cloudManager.QueueDeleteCloudRepositoryAsync(
            repository.CloudRepositoryId,
            $"DELETE {repository.CloudRepositoryId}");
        await RefreshAsync();
    }

    private async Task<bool> ConfirmCloudDeleteAsync(CloudRepositoryManagerRepositoryItemViewModel repository)
    {
        if (windows is null)
            return false;

        var owner = windows.GetActiveWindow();
        if (owner is null)
            return false;

        var dialog = windows.Create<ConfirmActionWindow>();
        if (dialog.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.Configure(
                Loc.T("cloud_manager.delete_title"),
                Loc.F("cloud_manager.delete_message", repository.Name, repository.CloudRepositoryId),
                Loc.F("cloud_manager.delete_warning", repository.CloudRepositoryId),
                Loc.T("cloud_manager.delete_from_cloud"));
        }

        await windows.ShowDialogAsync(dialog, owner);
        return dialog.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static string ResolveAccountName(string? displayName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(displayName)
            && !string.Equals(displayName.Trim(), email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return displayName.Trim();
        }

        return string.IsNullOrWhiteSpace(email)
            ? Loc.T("cloud_manager.not_signed_in")
            : email.Trim();
    }

    private static string LocalizeToken(string prefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Loc.T("cloud_manager.unknown");

        var key = $"{prefix}.{token.Trim().ToLowerInvariant()}";
        var value = Loc.T(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? Loc.F("common.unknown_value", HumanizeToken(token))
            : value;
    }

    private static string HumanizeToken(string token)
    {
        var text = token.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }
}

public sealed class CloudRepositoryManagerRepositoryItemViewModel
{
    public CloudRepositoryManagerRepositoryItemViewModel(CloudRepositoryManagerRepositoryDto source)
    {
        Source = source;
        CloudRepositoryId = source.CloudRepositoryId;
        Name = source.Name;
        CurrentLocalPath = string.IsNullOrWhiteSpace(source.CurrentLocalPath) ? source.OriginalPath : source.CurrentLocalPath;
        DisplayPath = string.IsNullOrWhiteSpace(CurrentLocalPath)
            ? Loc.T("cloud_restore.unknown_original_path")
            : CurrentLocalPath;
        RestoreStatus = LocalizeToken("cloud_manager.restore_status", source.RestoreStatus);
        ConflictStatus = LocalizeToken("cloud_manager.conflict_status", source.ConflictStatus);
        CloudIdText = Loc.F("cloud_manager.cloud_id", source.CloudRepositoryId);
        LatestSnapshotText = source.LatestSnapshotUtc is null
            ? Loc.T("cloud_manager.latest_snapshot_none")
            : Loc.F("cloud_manager.latest_snapshot", source.LatestSnapshotUtc.Value.ToLocalTime());
    }

    public CloudRepositoryManagerRepositoryDto Source { get; }
    public int CloudRepositoryId { get; }
    public string Name { get; }
    public string? CurrentLocalPath { get; }
    public string DisplayPath { get; }
    public string RestoreStatus { get; }
    public string ConflictStatus { get; }
    public string CloudIdText { get; }
    public string LatestSnapshotText { get; }

    private static string LocalizeToken(string prefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Loc.T("cloud_manager.unknown");

        var key = $"{prefix}.{token.Trim().ToLowerInvariant()}";
        var value = Loc.T(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? Loc.F("common.unknown_value", HumanizeToken(token))
            : value;
    }

    private static string HumanizeToken(string token)
    {
        var text = token.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }
}

public sealed class CloudRepositoryOperationItemViewModel
{
    public CloudRepositoryOperationItemViewModel(CloudRepositoryOperationStatusDto source)
    {
        OperationId = source.OperationId;
        OperationKind = LocalizeToken("cloud_manager.operation", source.OperationKind);
        Status = LocalizeToken("cloud_manager.operation_status", source.Status);
        Phase = LocalizeToken("cloud_manager.operation_phase", source.Phase);
        Percent = Math.Clamp(source.Percent, 0, 100);
        HasKnownPercent = source.Percent > 0 || source.Status is "completed" or "failed";
        PercentText = HasKnownPercent ? $"{Percent}%" : Loc.T("cloud_manager.progress_unknown");
        Error = source.Error ?? string.Empty;
        HasError = !string.IsNullOrWhiteSpace(Error);
        Summary = source.Status == "completed"
            ? Loc.T("cloud_manager.operation_completed_summary")
            : string.Empty;
    }

    public string OperationId { get; }
    public string OperationKind { get; }
    public string Status { get; }
    public string Phase { get; }
    public int Percent { get; }
    public bool HasKnownPercent { get; }
    public bool IsIndeterminate => !HasKnownPercent;
    public string PercentText { get; }
    public string Error { get; }
    public bool HasError { get; }
    public string Summary { get; }

    private static string LocalizeToken(string prefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Loc.T("cloud_manager.unknown");

        var key = $"{prefix}.{token.Trim().ToLowerInvariant()}";
        var value = Loc.T(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? Loc.F("common.unknown_value", HumanizeToken(token))
            : value;
    }

    private static string HumanizeToken(string token)
    {
        var text = token.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }
}
