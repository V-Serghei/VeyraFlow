using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Pages.RepositorySettings;

public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly IRepositoryCloudSyncOrchestrator _cloudSync;
    private readonly ILogger<RepositorySettingsViewModel> _log;

    private CancellationTokenSource? _retentionCts;

    public event Action? BackRequested;
    public event Func<int, Task>? RepositoryUpdated;
    public event Action? RepositoryDeleted;

    [ObservableProperty] private int _repositoryId;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _customFormat = string.Empty;

    [ObservableProperty] private bool _retentionEnabled;
    [ObservableProperty] private string _retentionMaxAgeDays = string.Empty;
    [ObservableProperty] private string _retentionMaxSnapshots = string.Empty;
    [ObservableProperty] private string _retentionMaxTotalSizeMb = string.Empty;
    [ObservableProperty] private string _retentionTriggerFilter = string.Empty;
    [ObservableProperty] private int _retentionRunIntervalMinutes = 60;
    [ObservableProperty] private string _retentionLastRunText = "Never";
    [ObservableProperty] private string _retentionLastStatusText = "-";

    [ObservableProperty] private string _syncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins;
    [ObservableProperty] private string _syncRetryMaxAttempts = "5";
    [ObservableProperty] private string _syncRetryBaseDelaySeconds = "30";
    [ObservableProperty] private string _cloudSyncStatusText = "-";
    [ObservableProperty] private string _cloudSyncLastSyncText = "Never";
    [ObservableProperty] private string _cloudSyncQueueText = "pending: 0, conflicts: 0";
    [ObservableProperty] private string _cloudSyncErrorText = string.Empty;
    [ObservableProperty] private bool _isSyncNowRunning;
    [ObservableProperty] private bool _isBundleOperationRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBundleOperationMessage))]
    private string _bundleOperationMessage = string.Empty;

    [ObservableProperty] private bool _isRetentionRunning;
    [ObservableProperty] private string _retentionProgressText = string.Empty;
    [ObservableProperty] private string _retentionResultText = string.Empty;

    public ObservableCollection<string> SelectedFormats { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<string> SyncConflictStrategies { get; } =
    [
        RepositorySyncConflictStrategies.LastWriteWins,
        RepositorySyncConflictStrategies.ManualMerge,
        RepositorySyncConflictStrategies.PreserveBoth
    ];

    public RepositorySettingsViewModel(
        IMediator mediator,
        IWindowService windows,
        IRepositoryCloudSyncOrchestrator cloudSync,
        ILogger<RepositorySettingsViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _cloudSync = cloudSync;
        _log = log;
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = "Repository was not found.";
                return;
            }

            var allFormats = await _mediator.Send(new GetTrackedExtensionsQuery());

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            Description = repo.Description;
            DirectoryPath = repo.DirectoryPath;

            SelectedFormats.Clear();
            foreach (var format in repo.LinkedFormats.OrderBy(x => x))
                SelectedFormats.Add(format);

            AvailableFormats.Clear();
            foreach (var format in allFormats.OrderBy(x => x))
                AvailableFormats.Add(format);

            ApplyRetentionPolicy(repo.RetentionPolicy);
            ApplyCloudSyncStatus(repo.CloudSync);

            RetentionResultText = string.Empty;
            RetentionProgressText = string.Empty;
            BundleOperationMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repository settings for {RepositoryId}", repositoryId);
            ErrorMessage = "Failed to load repository settings.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var res = await owner.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select repository directory",
                AllowMultiple = false
            });

        var local = res.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
            DirectoryPath = local;
    }

    [RelayCommand]
    private void AddAvailableFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var normalized = NormalizeFormat(format);
        if (SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;

        SelectedFormats.Add(normalized);
    }

    [RelayCommand]
    private void AddCustomFormat()
    {
        var normalized = NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!AvailableFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            AvailableFormats.Add(normalized);

        if (!SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            SelectedFormats.Add(normalized);

        CustomFormat = string.Empty;
    }

    [RelayCommand]
    private void RemoveFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var match = SelectedFormats.FirstOrDefault(f =>
            f.Equals(format, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            SelectedFormats.Remove(match);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var policy = BuildRetentionPolicyFromState();
            var syncRetryAttempts = ParseIntOrDefault(SyncRetryMaxAttempts, 5, 1, 20);
            var syncRetryDelay = ParseIntOrDefault(SyncRetryBaseDelaySeconds, 30, 5, 600);
            var strategy = RepositorySyncConflictStrategies.Normalize(SyncConflictStrategy);

            var result = await _mediator.Send(new UpdateRepositoryConfigurationCommand(
                RepositoryId,
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                policy,
                strategy,
                syncRetryAttempts,
                syncRetryDelay));

            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Failed to save repository settings.";
                return;
            }

            await _cloudSync.ProcessPendingQueueAsync();

            if (RepositoryUpdated is not null)
                await RepositoryUpdated.Invoke(RepositoryId);

            await LoadAsync(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository settings for {RepositoryId}", RepositoryId);
            ErrorMessage = "Failed to save repository settings.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (RepositoryId <= 0)
            return;

        try
        {
            IsSyncNowRunning = true;
            ErrorMessage = null;

            await _cloudSync.TryPushLatestSnapshotAsync(RepositoryId);
            await _cloudSync.ProcessPendingQueueAsync();

            await LoadAsync(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run cloud sync for repository {RepositoryId}", RepositoryId);
            ErrorMessage = "Cloud sync failed.";
        }
        finally
        {
            IsSyncNowRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleOperations))]
    private async Task ExportBundleAsync()
    {
        if (!CanRunBundleOperations)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = "Unable to open file picker window.";
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export repository bundle",
            SuggestedFileName = BuildSuggestedBundleFileName(),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Veyra bundle")
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        await RunBundleOperationAsync(
            startedMessage: "Exporting repository bundle...",
            operation: async () => await _mediator.Send(new ExportRepositoryBundleCommand(RepositoryId, bundlePath)),
            onSuccess: result =>
                $"{result.Summary}\nBundle: {result.BundlePath}\nSize: {FormatSize(result.BundleSizeBytes)}");
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleOperations))]
    private async Task ImportBundleAsync()
    {
        if (!CanRunBundleOperations)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = "Unable to open file picker window.";
            return;
        }

        var bundleSelection = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import repository bundle",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Veyra bundle")
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = bundleSelection.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        var validation = await _mediator.Send(new ValidateRepositoryBundleQuery(bundlePath));
        if (!validation.IsValid)
        {
            ErrorMessage = validation.Message;
            BundleOperationMessage = validation.Message;
            return;
        }

        var targetDirectorySelection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select target directory for imported repository",
            AllowMultiple = false
        });

        var targetDirectory = targetDirectorySelection.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        await RunBundleOperationAsync(
            startedMessage: "Importing repository bundle...",
            operation: async () => await _mediator.Send(new ImportRepositoryBundleCommand(
                bundlePath,
                targetDirectory,
                RepositoryNameOverride: null)),
            onSuccess: result =>
            {
                var warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : "\nWarnings:\n" + string.Join('\n', result.Warnings);

                return $"{result.Summary}\nImported repository id: {result.RepositoryId}{warningText}";
            });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            await _mediator.Send(new DeleteRepositoryCommand(RepositoryId));
            RepositoryDeleted?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository {RepositoryId}", RepositoryId);
            ErrorMessage = "Failed to delete repository.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRetention))]
    private Task RunRetentionDryRunAsync() => RunRetentionAsync(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanRunRetention))]
    private Task RunRetentionApplyAsync() => RunRetentionAsync(dryRun: false);

    [RelayCommand(CanExecute = nameof(CanCancelRetention))]
    private void CancelRetention()
    {
        _retentionCts?.Cancel();
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    public bool HasBundleOperationMessage => !string.IsNullOrWhiteSpace(BundleOperationMessage);
    public bool CanRunBundleOperations => RepositoryId > 0 && !IsLoading && !IsBundleOperationRunning;

    public bool CanRunRetention => RepositoryId > 0 && RetentionEnabled && !IsRetentionRunning;
    public bool CanCancelRetention => IsRetentionRunning;

    partial void OnIsRetentionRunningChanged(bool value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        CancelRetentionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanCancelRetention));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunBundleOperations));
    }

    partial void OnIsBundleOperationRunningChanged(bool value)
    {
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunBundleOperations));
    }

    partial void OnRepositoryIdChanged(int value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanRunBundleOperations));
    }

    partial void OnRetentionEnabledChanged(bool value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
    }


    private async Task RunBundleOperationAsync<TResult>(
        string startedMessage,
        Func<Task<OperationResult<TResult>>> operation,
        Func<TResult, string> onSuccess)
        where TResult : class
    {
        if (!CanRunBundleOperations)
            return;

        try
        {
            IsBundleOperationRunning = true;
            ErrorMessage = null;
            BundleOperationMessage = startedMessage;

            var result = await operation();
            if (!result.Success || result.Value is null)
            {
                var message = result.Error ?? "Bundle operation failed.";
                ErrorMessage = message;
                BundleOperationMessage = message;
                return;
            }

            BundleOperationMessage = onSuccess(result.Value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bundle operation failed for repository {RepositoryId}", RepositoryId);
            ErrorMessage = "Bundle operation failed.";
            BundleOperationMessage = ErrorMessage;
        }
        finally
        {
            IsBundleOperationRunning = false;
        }
    }
    private async Task RunRetentionAsync(bool dryRun)
    {
        if (!CanRunRetention)
            return;

        try
        {
            IsRetentionRunning = true;
            RetentionResultText = string.Empty;
            RetentionProgressText = "Running...";
            ErrorMessage = null;

            _retentionCts?.Dispose();
            _retentionCts = new CancellationTokenSource();

            var progress = new Progress<RepositoryRetentionProgressDto>(p =>
            {
                RetentionProgressText = $"{p.Percent}% {p.Message}";
            });

            var result = await _mediator.Send(
                new RunRepositoryRetentionCommand(RepositoryId, dryRun, progress),
                _retentionCts.Token);

            if (!result.Success || result.Value is null)
            {
                RetentionResultText = result.Error ?? "Failed to run retention.";
                return;
            }

            RetentionResultText = result.Value.Summary;
            RetentionProgressText = "Completed.";

            if (!dryRun)
            {
                await LoadAsync(RepositoryId);
            }
        }
        catch (OperationCanceledException)
        {
            RetentionProgressText = "Operation cancelled.";
            RetentionResultText = "Retention run was cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run retention for repository {RepositoryId}", RepositoryId);
            RetentionResultText = "Retention failed with an exception.";
            RetentionProgressText = string.Empty;
        }
        finally
        {
            IsRetentionRunning = false;
            _retentionCts?.Dispose();
            _retentionCts = null;
        }
    }

    private void ApplyRetentionPolicy(RepositoryRetentionPolicyDto policy)
    {
        RetentionEnabled = policy.Enabled;
        RetentionMaxAgeDays = policy.MaxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RetentionMaxSnapshots = policy.MaxSnapshots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RetentionMaxTotalSizeMb = policy.MaxTotalSizeBytes is > 0
            ? Math.Round(policy.MaxTotalSizeBytes.Value / 1024d / 1024d, 2).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        RetentionTriggerFilter = policy.TriggerFilters.Count == 0
            ? string.Empty
            : string.Join(", ", policy.TriggerFilters);
        RetentionRunIntervalMinutes = Math.Clamp(policy.RunIntervalMinutes, 5, 7 * 24 * 60);
        RetentionLastRunText = policy.LastRunAtUtc is null
            ? "Never"
            : policy.LastRunAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RetentionLastStatusText = string.IsNullOrWhiteSpace(policy.LastStatus)
            ? "-"
            : policy.LastStatus;
    }

    private void ApplyCloudSyncStatus(RepositoryCloudSyncStatusDto? status)
    {
        if (status is null)
        {
            SyncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins;
            SyncRetryMaxAttempts = "5";
            SyncRetryBaseDelaySeconds = "30";
            CloudSyncStatusText = "-";
            CloudSyncLastSyncText = "Never";
            CloudSyncQueueText = "pending: 0, conflicts: 0";
            CloudSyncErrorText = string.Empty;
            return;
        }

        SyncConflictStrategy = RepositorySyncConflictStrategies.Normalize(status.ConflictStrategy);
        SyncRetryMaxAttempts = status.RetryMaxAttempts.ToString(CultureInfo.InvariantCulture);
        SyncRetryBaseDelaySeconds = status.RetryBaseDelaySeconds.ToString(CultureInfo.InvariantCulture);
        CloudSyncStatusText = FormatCloudSyncStatus(status.LastStatus);
        CloudSyncLastSyncText = status.LastSyncedAtUtc is null
            ? "Never"
            : status.LastSyncedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        CloudSyncQueueText = $"pending: {status.PendingQueueCount}, conflicts: {status.ConflictQueueCount}";
        CloudSyncErrorText = status.LastError ?? string.Empty;
    }

    private RepositoryRetentionPolicyDto BuildRetentionPolicyFromState()
    {
        var maxAgeDays = ParseNullableInt(RetentionMaxAgeDays);
        var maxSnapshots = ParseNullableInt(RetentionMaxSnapshots);
        var maxTotalSizeMb = ParseNullableDouble(RetentionMaxTotalSizeMb);
        long? maxTotalSizeBytes = null;

        if (maxTotalSizeMb is > 0)
            maxTotalSizeBytes = (long)Math.Round(maxTotalSizeMb.Value * 1024d * 1024d, MidpointRounding.AwayFromZero);

        var triggers = RetentionTriggerFilter
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RepositoryRetentionPolicyDto(
            RetentionEnabled,
            maxAgeDays,
            maxSnapshots,
            maxTotalSizeBytes,
            triggers,
            Math.Clamp(RetentionRunIntervalMinutes, 5, 7 * 24 * 60),
            LastRunAtUtc: null,
            LastStatus: null);
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
            "auth_required" => "Authorization required",
            "conflict" => "Conflict detected",
            "failed" => "Failed",
            "skipped" => "No upload needed",
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => status,
            _ => status.Replace('_', ' ')
        };
    }
    private string BuildSuggestedBundleFileName()
    {
        var safeName = string.IsNullOrWhiteSpace(RepositoryName)
            ? $"repo_{RepositoryId}"
            : SanitizeFileName(RepositoryName);

        return $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.veyra.zip";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var normalized = new string(chars).Trim('_', ' ');

        return string.IsNullOrWhiteSpace(normalized)
            ? "repository"
            : normalized;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
    private static string NormalizeFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
    }

    private static int ParseIntOrDefault(string value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return fallback;

        return Math.Clamp(parsed, min, max);
    }

    private static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }
}

