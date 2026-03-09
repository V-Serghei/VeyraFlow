using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Queries;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryDashboardViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILogger<RepositoryDashboardViewModel> _log;
    private readonly IWindowService _windows;
    private readonly List<RepositoryCardViewModel> _allRepositories = [];

    public event Func<int, Task>? OpenRepositoryRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;

    public ObservableCollection<RepositoryCardViewModel> Repositories { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _searchQuery = string.Empty;

    public RepositoryDashboardViewModel(
        IMediator mediator,
        ILogger<RepositoryDashboardViewModel> log,
        IWindowService windows)
    {
        _mediator = mediator;
        _log = log;
        _windows = windows;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await _mediator.Send(new EnsureRepositoriesCommand());

            var repos = await _mediator.Send(new GetAllRepositoriesQuery());
            _allRepositories.Clear();

            foreach (var r in repos)
            {
                var isAvailable = Directory.Exists(r.DirectoryPath);
                var card = new RepositoryCardViewModel
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    DirectoryPath = r.DirectoryPath,
                    StatusText = isAvailable ? "Local" : "Unavailable",
                    StatusColor = isAvailable ? "#4CAF50" : "#F44336",
                    LastActivity = FormatLastActivity(r.LastScannedAt),
                    FileCount = r.FileCount,
                    VersionCount = r.VersionCount,
                    SizeDisplay = FormatSize(r.TotalSizeBytes),
                    CloudSyncStatus = FormatCloudSyncStatus(r.CloudSync?.LastStatus),
                    CloudQueueSummary = $"pending {r.CloudSync?.PendingQueueCount ?? 0} / conflicts {r.CloudSync?.ConflictQueueCount ?? 0}"
                };

                foreach (var f in r.LinkedFormats)
                    card.LinkedFormats.Add(f);

                card.RefreshFormatsDisplay();
                _allRepositories.Add(card);
            }

            ApplyFilter();
            IsEmpty = Repositories.Count == 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repositories");
            ErrorMessage = "Failed to load repositories.";
            Repositories.Clear();
            IsEmpty = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task OpenRepositoryAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        if (OpenRepositoryRequested is not null)
            await OpenRepositoryRequested.Invoke(repo.Id);
    }

    [RelayCommand]
    private async Task OpenRepositorySettingsAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        if (OpenRepositorySettingsRequested is not null)
            await OpenRepositorySettingsRequested.Invoke(repo.Id);
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        try
        {
            var wizard = _windows.Create<CreateRepositoryWindow>();
            var owner = _windows.GetActiveWindow();

            if (owner is not null)
                await _windows.ShowDialogAsync(wizard, owner);
            else
                _windows.Show(wizard);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open repository creation wizard");
            ErrorMessage = "Failed to open repository creation wizard.";
        }
    }

    [RelayCommand]
    private async Task DeleteRepositoryAsync(RepositoryCardViewModel? repo)
    {
        if (repo is null)
            return;

        try
        {
            await _mediator.Send(new DeleteRepositoryCommand(repo.Id));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository");
            ErrorMessage = "Failed to delete repository.";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();

        IEnumerable<RepositoryCardViewModel> source = _allRepositories;
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(r =>
                r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.DirectoryPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.LinkedFormats.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        Repositories.Clear();
        foreach (var repo in source.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            Repositories.Add(repo);

        IsEmpty = Repositories.Count == 0;
    }

    private static string FormatLastActivity(DateTime? utc)
    {
        if (utc is null)
            return "No scan";

        var delta = DateTime.UtcNow - utc.Value;
        if (delta.TotalSeconds < 60) return "Just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
        return $"{(int)delta.TotalDays} d ago";
    }

    private static string FormatCloudSyncStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "Idle";

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" => "Queued",
            "syncing" => "Syncing",
            "offline_retry" => "Offline, retry scheduled",
            "retrying" => "Retrying",
            "auth_required" => "Auth required",
            "conflict" => "Conflict",
            "failed" => "Failed",
            "skipped" => "No upload needed",
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => status,
            _ => status.Replace('_', ' ')
        };
    }
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
