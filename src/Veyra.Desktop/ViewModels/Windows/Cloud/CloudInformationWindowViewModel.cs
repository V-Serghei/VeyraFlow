using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class CloudInformationWindowViewModel(
    ICloudRepositoryManagementService cloudManager,
    IWindowService windows) : ObservableObject
{
    [ObservableProperty] private string _accountText = Loc.T("cloud_manager.not_signed_in");
    [ObservableProperty] private string _connectionText = Loc.T("cloud_manager.unknown");
    [ObservableProperty] private string _lastSyncText = Loc.T("cloud_info.never");
    [ObservableProperty] private string _storageText = "0 B";
    [ObservableProperty] private string _repositoryCountText = "0";
    [ObservableProperty] private string _snapshotCountText = "0";
    [ObservableProperty] private string _blockCountText = "0";
    [ObservableProperty] private string _operationText = Loc.T("cloud_info.no_pending_operations");
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCloudUnavailable;
    [ObservableProperty] private string _cloudWarningTitle = string.Empty;
    [ObservableProperty] private string _cloudWarningDetail = string.Empty;

    public ObservableCollection<CloudRepositoryManagerRepositoryDto> CloudOnlyRepositories { get; } = [];
    public ObservableCollection<CloudRepositoryManagerRepositoryDto> LinkedRepositories { get; } = [];
    public ObservableCollection<CloudRepositoryManagerRepositoryDto> ConflictedRepositories { get; } = [];

    public event Action? RequestClose;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var overview = await cloudManager.GetOverviewAsync();
            AccountText = string.IsNullOrWhiteSpace(overview.AccountEmail)
                ? overview.AccountDisplayName ?? Loc.T("cloud_manager.not_signed_in")
                : string.IsNullOrWhiteSpace(overview.AccountDisplayName)
                    || string.Equals(overview.AccountDisplayName.Trim(), overview.AccountEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? overview.AccountEmail.Trim()
                        : $"{overview.AccountDisplayName.Trim()}\n{overview.AccountEmail.Trim()}";
            ConnectionText = LocalizeToken("cloud_manager.connection", overview.ConnectionState);

            var state = overview.ConnectionState?.Trim().ToLowerInvariant();
            IsCloudUnavailable = state is "cloudunavailable" or "internetunavailable";
            CloudWarningTitle = state switch
            {
                "internetunavailable" => Loc.T("dashboard.mode.internet_unavailable_title"),
                "cloudunavailable"    => Loc.T("dashboard.mode.cloud_unavailable_title"),
                _                     => string.Empty
            };
            CloudWarningDetail = state switch
            {
                "internetunavailable" => Loc.T("dashboard.mode.internet_unavailable_detail"),
                "cloudunavailable"    => Loc.T("dashboard.mode.cloud_unavailable_detail"),
                _                     => string.Empty
            };

            LastSyncText = overview.LastSuccessfulSyncUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Loc.T("cloud_info.never");
            StorageText = FormatBytes(overview.CloudStorageUsedBytes);
            RepositoryCountText = overview.CloudRepositoryCount.ToString();
            SnapshotCountText = overview.CloudSnapshotCount.ToString();
            BlockCountText = overview.CloudBlockCount.ToString();
            OperationText = Loc.F("cloud_info.operation_counts", overview.PendingOperationCount, overview.FailedOperationCount);

            Replace(CloudOnlyRepositories, overview.Repositories.Where(r => !r.HasLocalLink));
            Replace(LinkedRepositories, overview.Repositories.Where(r => r.HasLocalLink));
            Replace(ConflictedRepositories, overview.Repositories.Where(r => !string.Equals(r.ConflictStatus, "none", StringComparison.OrdinalIgnoreCase)));
            StatusText = Loc.T("cloud_info.up_to_date");
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("cloud_info.refresh_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenManagerAsync()
    {
        var owner = windows.GetActiveWindow();
        var manager = windows.Create<CloudRepositoryManagerWindow>();
        if (manager.DataContext is CloudRepositoryManagerWindowViewModel vm)
            await vm.RefreshAsync();

        if (owner is null)
            windows.Show(manager);
        else
            await windows.ShowDialogAsync(manager, owner);
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
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
