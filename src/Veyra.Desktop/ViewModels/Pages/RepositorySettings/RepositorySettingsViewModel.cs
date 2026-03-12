using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.RepositorySettings;

public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly IRepositoryCloudSyncOrchestrator _cloudSync;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly ILogger<RepositorySettingsViewModel> _log;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;

    private CancellationTokenSource? _retentionCts;
    private RepositoryRetentionPolicyDto? _lastAppliedRetentionPolicy;
    private RepositoryCloudSyncStatusDto? _lastAppliedCloudSyncStatus;
    private readonly HashSet<string> _allFormatOptions = new(StringComparer.OrdinalIgnoreCase);

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
    [ObservableProperty] private string _retentionLastRunText = "";
    [ObservableProperty] private string _retentionLastStatusText = "";

    [ObservableProperty] private string _syncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins;
    [ObservableProperty] private string _syncRetryMaxAttempts = "5";
    [ObservableProperty] private string _syncRetryBaseDelaySeconds = "30";
    [ObservableProperty] private string _cloudSyncStatusText = "";
    [ObservableProperty] private string _cloudSyncLastSyncText = "";
    [ObservableProperty] private string _cloudSyncQueueText = "";
    [ObservableProperty] private string _cloudSyncErrorText = string.Empty;
    [ObservableProperty] private bool _isSyncNowRunning;
    [ObservableProperty] private bool _isCloudRepairRunning;
    [ObservableProperty] private bool _isBundleOperationRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloudRepairMessage))]
    private string _cloudRepairMessage = string.Empty;

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
        ISensitiveActionGuard sensitiveActionGuard,
        ILogger<RepositorySettingsViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _cloudSync = cloudSync;
        _sensitiveActionGuard = sensitiveActionGuard;
        _log = log;
        SelectedFormats.CollectionChanged += OnSelectedFormatsCollectionChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshLocalizationState();
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            _log.LogInformation("Loading repository settings. RepositoryId {RepositoryId}", repositoryId);

            var repo = await _mediator.Send(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = Loc.T("repo_settings.error_not_found");
                return;
            }

            var allFormats = await _mediator.Send(new GetTrackedExtensionsQuery());

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            Description = repo.Description;
            DirectoryPath = repo.DirectoryPath;

            _allFormatOptions.Clear();
            foreach (var format in allFormats
                         .Select(NormalizeFormat)
                         .Where(static format => !string.IsNullOrWhiteSpace(format))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static format => format, StringComparer.OrdinalIgnoreCase))
            {
                _allFormatOptions.Add(format);
            }

            SelectedFormats.Clear();
            foreach (var format in repo.LinkedFormats
                         .Select(NormalizeFormat)
                         .Where(static format => !string.IsNullOrWhiteSpace(format))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static format => format, StringComparer.OrdinalIgnoreCase))
            {
                _allFormatOptions.Add(format);
                SelectedFormats.Add(format);
            }

            RefreshAvailableFormats();

            ApplyRetentionPolicy(repo.RetentionPolicy);
            ApplyCloudSyncStatus(repo.CloudSync);

            RetentionResultText = string.Empty;
            RetentionProgressText = string.Empty;
            BundleOperationMessage = string.Empty;
            _log.LogInformation(
                "Repository settings loaded. RepositoryId {RepositoryId}. SelectedFormats {SelectedFormatsCount}",
                RepositoryId,
                SelectedFormats.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load repository settings for {RepositoryId}", repositoryId);
            ErrorMessage = Loc.T("repo_settings.error_load_failed");
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
                Title = Loc.T("repo_settings.picker_select_directory"),
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

        _allFormatOptions.Add(normalized);
        SelectedFormats.Add(normalized);
    }

    [RelayCommand]
    private void AddCustomFormat()
    {
        var normalized = NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _allFormatOptions.Add(normalized);
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
            _log.LogInformation("Saving repository settings. RepositoryId {RepositoryId}", RepositoryId);

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
                ErrorMessage = result.Error ?? Loc.T("repo_settings.error_save_failed");
                return;
            }

            await _cloudSync.ProcessPendingQueueAsync();

            if (RepositoryUpdated is not null)
                await RepositoryUpdated.Invoke(RepositoryId);

            await LoadAsync(RepositoryId);
            _log.LogInformation("Repository settings saved. RepositoryId {RepositoryId}", RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository settings for {RepositoryId}", RepositoryId);
            ErrorMessage = Loc.T("repo_settings.error_save_failed");
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
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_cloud_sync"),
                Loc.F("security.action_cloud_sync_body", RepositoryName));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncNowRunning = true;
            ErrorMessage = null;
            _log.LogInformation("Repository cloud sync requested. RepositoryId {RepositoryId}", RepositoryId);

            await _cloudSync.TryPushLatestSnapshotAsync(RepositoryId);
            await _cloudSync.ProcessPendingQueueAsync();

            await LoadAsync(RepositoryId);
            _log.LogInformation("Repository cloud sync finished. RepositoryId {RepositoryId}", RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run cloud sync for repository {RepositoryId}", RepositoryId);
            ErrorMessage = Loc.T("repo_settings.error_sync_failed");
        }
        finally
        {
            IsSyncNowRunning = false;
        }
    }

    [RelayCommand]
    private async Task RepairCloudDataAsync()
    {
        if (RepositoryId <= 0)
            return;

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_repository_cloud_repair"),
                Loc.F("security.action_repository_cloud_repair_body", RepositoryName));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsCloudRepairRunning = true;
            ErrorMessage = null;
            CloudRepairMessage = Loc.T("repo_settings.cloud_repair_running");
            _log.LogInformation("Repository cloud repair requested. RepositoryId {RepositoryId}", RepositoryId);

            var result = await _cloudSync.RepairRepositoryCloudDataAsync(RepositoryId);
            await LoadAsync(RepositoryId);

            CloudRepairMessage = result.Success
                ? Loc.F(
                    "repo_settings.cloud_repair_finished",
                    result.ReferencedBlocks,
                    result.AlreadyPresentBlocks,
                    result.UploadedBlocks,
                    result.MissingLocalBlocks)
                : Loc.F(
                    "repo_settings.cloud_repair_finished_with_errors",
                    result.ReferencedBlocks,
                    result.AlreadyPresentBlocks,
                    result.UploadedBlocks,
                    result.MissingLocalBlocks,
                    result.FailedUploads);

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && !result.Success)
                ErrorMessage = result.ErrorMessage;

            _log.LogInformation(
                "Repository cloud repair finished. RepositoryId {RepositoryId}. Success {Success}. ReferencedBlocks {ReferencedBlocks}. UploadedBlocks {UploadedBlocks}. MissingLocalBlocks {MissingLocalBlocks}. FailedUploads {FailedUploads}",
                RepositoryId,
                result.Success,
                result.ReferencedBlocks,
                result.UploadedBlocks,
                result.MissingLocalBlocks,
                result.FailedUploads);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to repair cloud data for repository {RepositoryId}", RepositoryId);
            ErrorMessage = Loc.T("repo_settings.cloud_repair_failed");
            CloudRepairMessage = ErrorMessage;
        }
        finally
        {
            IsCloudRepairRunning = false;
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
            ErrorMessage = Loc.T("repo_settings.error_picker_unavailable");
            return;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("repo_settings.bundle_export_title"),
            SuggestedFileName = BuildSuggestedBundleFileName(),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(Loc.T("repo_settings.bundle_file_type"))
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var bundlePath = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(bundlePath))
            return;

        await RunBundleOperationAsync(
            startedMessage: Loc.T("repo_settings.bundle_exporting"),
            operation: async () => await _mediator.Send(new ExportRepositoryBundleCommand(RepositoryId, bundlePath)),
            onSuccess: result =>
                $"{result.Summary}\n{Loc.T("repo_settings.bundle_path_label")}: {result.BundlePath}\n{Loc.T("common.size")}: {FormatSize(result.BundleSizeBytes)}");
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleOperations))]
    private async Task ImportBundleAsync()
    {
        if (!CanRunBundleOperations)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            ErrorMessage = Loc.T("repo_settings.error_picker_unavailable");
            return;
        }

        var bundleSelection = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("repo_settings.bundle_import_title"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("repo_settings.bundle_file_type"))
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
            Title = Loc.T("repo_settings.bundle_import_target_title"),
            AllowMultiple = false
        });

        var targetDirectory = targetDirectorySelection.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        await RunBundleOperationAsync(
            startedMessage: Loc.T("repo_settings.bundle_importing"),
            operation: async () => await _mediator.Send(new ImportRepositoryBundleCommand(
                bundlePath,
                targetDirectory,
                RepositoryNameOverride: null)),
            onSuccess: result =>
            {
                var warningText = result.Warnings.Count == 0
                    ? string.Empty
                    : $"\n{Loc.T("repo_settings.bundle_warnings")}:\n" + string.Join('\n', result.Warnings);

                return $"{result.Summary}\n{Loc.T("repo_settings.bundle_imported_repository_id")}: {result.RepositoryId}{warningText}";
            });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            if (!await ConfirmRepositoryDeletionAsync())
                return;

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_delete_repository"),
                Loc.F("security.action_delete_repository_body", RepositoryName));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            await _mediator.Send(new DeleteRepositoryCommand(RepositoryId));
            RepositoryDeleted?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete repository {RepositoryId}", RepositoryId);
            ErrorMessage = Loc.T("repo_settings.error_delete_failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRetention))]
    private Task RunRetentionDryRunAsync() => RunRetentionAsync(dryRun: true);

    private void OnSelectedFormatsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshAvailableFormats();
    }

    private void RefreshAvailableFormats()
    {
        var selected = new HashSet<string>(SelectedFormats, StringComparer.OrdinalIgnoreCase);
        var available = _allFormatOptions
            .Where(format => !selected.Contains(format))
            .OrderBy(format => format, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableFormats.Clear();
        foreach (var format in available)
            AvailableFormats.Add(format);
    }

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
    public bool HasCloudRepairMessage => !string.IsNullOrWhiteSpace(CloudRepairMessage);
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
                var message = result.Error ?? Loc.T("repo_settings.error_bundle_failed");
                ErrorMessage = message;
                BundleOperationMessage = message;
                return;
            }

            BundleOperationMessage = onSuccess(result.Value);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bundle operation failed for repository {RepositoryId}", RepositoryId);
            ErrorMessage = Loc.T("repo_settings.error_bundle_failed");
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
            RetentionProgressText = Loc.T("repo_settings.retention_running");
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
                RetentionResultText = result.Error ?? Loc.T("repo_settings.retention_failed");
                return;
            }

            RetentionResultText = result.Value.Summary;
            RetentionProgressText = Loc.T("repo_settings.retention_completed");

            if (!dryRun)
            {
                await LoadAsync(RepositoryId);
            }
        }
        catch (OperationCanceledException)
        {
            RetentionProgressText = Loc.T("repo_settings.retention_cancelled");
            RetentionResultText = Loc.T("repo_settings.retention_cancelled_result");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run retention for repository {RepositoryId}", RepositoryId);
            RetentionResultText = Loc.T("repo_settings.retention_failed_exception");
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
        _lastAppliedRetentionPolicy = policy;
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
        RetentionLastRunText = FormatNeverOrDate(policy.LastRunAtUtc);
        RetentionLastStatusText = string.IsNullOrWhiteSpace(policy.LastStatus)
            ? Loc.T("common.not_available_short")
            : policy.LastStatus;
    }

    private void ApplyCloudSyncStatus(RepositoryCloudSyncStatusDto? status)
    {
        _lastAppliedCloudSyncStatus = status;
        if (status is null)
        {
            SyncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins;
            SyncRetryMaxAttempts = "5";
            SyncRetryBaseDelaySeconds = "30";
            CloudSyncStatusText = Loc.T("dashboard.sync.idle");
            CloudSyncLastSyncText = Loc.T("common.never");
            CloudSyncQueueText = FormatCloudQueueSummary(0, 0, 0, 0, 0);
            CloudSyncErrorText = string.Empty;
            return;
        }

        SyncConflictStrategy = RepositorySyncConflictStrategies.Normalize(status.ConflictStrategy);
        SyncRetryMaxAttempts = status.RetryMaxAttempts.ToString(CultureInfo.InvariantCulture);
        SyncRetryBaseDelaySeconds = status.RetryBaseDelaySeconds.ToString(CultureInfo.InvariantCulture);
        CloudSyncStatusText = FormatCloudSyncStatus(status.LastStatus);
        CloudSyncLastSyncText = FormatNeverOrDate(status.LastSyncedAtUtc);
        CloudSyncQueueText = FormatCloudQueueSummary(
            status.PendingQueueCount,
            status.RunningQueueCount,
            status.RetryQueueCount,
            status.ConflictQueueCount,
            status.DeadLetterQueueCount);
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
            return Loc.T("dashboard.sync.idle");

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" => Loc.T("dashboard.sync.queued"),
            "syncing" => Loc.T("dashboard.sync.syncing"),
            "offline_retry" => Loc.T("dashboard.sync.offline_retry"),
            "retrying" => Loc.T("dashboard.sync.retrying"),
            "auth_required" => Loc.T("dashboard.sync.auth_required"),
            "conflict" => Loc.T("dashboard.sync.conflict"),
            "failed" => Loc.T("dashboard.sync.failed"),
            "skipped" => Loc.T("dashboard.sync.skipped"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => status,
            _ => status.Replace('_', ' ')
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        var selectedStrategy = SyncConflictStrategy;
        SyncConflictStrategies.Clear();
        foreach (var strategy in RepositorySyncConflictStrategies.All)
            SyncConflictStrategies.Add(strategy);
        SyncConflictStrategy = RepositorySyncConflictStrategies.All.Contains(selectedStrategy, StringComparer.OrdinalIgnoreCase)
            ? selectedStrategy
            : RepositorySyncConflictStrategies.LastWriteWins;

        if (_lastAppliedRetentionPolicy is not null)
            ApplyRetentionPolicy(_lastAppliedRetentionPolicy);
        else
            RetentionLastRunText = Loc.T("common.never");

        ApplyCloudSyncStatus(_lastAppliedCloudSyncStatus);
    }

    private static string FormatNeverOrDate(DateTime? value)
        => value is null
            ? Loc.T("common.never")
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatCloudQueueSummary(int pending, int running, int retry, int conflict, int deadLetter)
        => Loc.F("dashboard.queue_summary", pending, running, retry, conflict, deadLetter);

    private async Task<bool> ConfirmRepositoryDeletionAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.Configure(
                Loc.T("repo_settings.delete_confirm_title"),
                Loc.F("repo_settings.delete_confirm_body", RepositoryName),
                Loc.T("repo_settings.delete_confirm_warning"),
                Loc.T("repo_settings.delete_confirm_button"));
        }

        await _windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
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

