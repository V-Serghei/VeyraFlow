using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Files;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Models.TrackedFormats;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Connectivity.Models;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.Services.Sync.Runtime;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.RepositorySettings;

public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    private const string SafeDefaultRetentionTriggerFilter = "automatic";
    private static readonly TimeSpan CloudSyncStatusRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly IWindowService _windows;
    private readonly IRepositoryCloudSyncOrchestrator _cloudSync;
    private readonly ICloudSyncService _cloudSyncService;
    private readonly IConnectivityStatusService _connectivity;
    private readonly ICloudSyncRuntimeControlService _cloudSyncRuntime;
    private readonly IAccessTokenPolicyService _tokenPolicy;
    private readonly IUserProfileRepository _userProfiles;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly ILogger<RepositorySettingsViewModel> _log;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly UserExperienceManager _experience = UserExperienceManager.Instance;

    private CancellationTokenSource? _retentionCts;
    private RepositoryRetentionPolicyDto? _lastAppliedRetentionPolicy;
    private RepositoryCloudSyncStatusDto? _lastAppliedCloudSyncStatus;
    private readonly HashSet<string> _allFormatOptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _manualRetentionCleanupConfirmedForCurrentPolicy;
    private bool _syncingManualHistorySafeMode;
    private bool _syncingRetentionTriggerSelection;
    private CancellationTokenSource? _cloudSyncStatusRefreshCts;
    private Task? _cloudSyncStatusRefreshTask;
    private RepositorySettingsSnapshot? _lastSavedSettingsSnapshot;

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
    [ObservableProperty] private string _customExclusionPattern = string.Empty;
    [ObservableProperty] private bool _autoCaptureFileVersions;
    [ObservableProperty] private bool _protectCloudMetadata;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionEffectiveSourceText))]
    [NotifyPropertyChangedFor(nameof(RetentionInheritanceHintText))]
    [NotifyPropertyChangedFor(nameof(CanEditLocalRetentionPolicy))]
    private bool _retentionUseLocalPolicy = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionEffectiveSourceText))]
    [NotifyPropertyChangedFor(nameof(RetentionInheritanceHintText))]
    private string _retentionPolicySource = RepositoryRetentionPolicySources.Repository;

    [ObservableProperty] private bool _retentionEnabled;
    [ObservableProperty] private string _retentionMaxAgeDays = string.Empty;
    [ObservableProperty] private string _retentionMaxSnapshots = string.Empty;
    [ObservableProperty] private string _retentionMaxTotalSizeMb = string.Empty;
    [ObservableProperty] private string _retentionTriggerFilter = string.Empty;
    [ObservableProperty] private bool _retentionIncludesAutomaticSnapshots = true;
    [ObservableProperty] private bool _retentionIncludesManualSnapshots;
    [ObservableProperty] private bool _retentionIncludesWorkingSnapshots;
    [ObservableProperty] private int _retentionRunIntervalMinutes = 60;
    [ObservableProperty] private bool _retentionMaintenanceWindowEnabled;
    [ObservableProperty] private int _selectedRetentionMaintenanceWindowStartHour = 1;
    [ObservableProperty] private int _selectedRetentionMaintenanceWindowEndHour = 5;
    [ObservableProperty] private bool _retentionArchiveMode;
    [ObservableProperty] private bool _retentionManualHistorySafeMode = true;
    [ObservableProperty] private bool _retentionManualCleanupAllowed;
    [ObservableProperty] private bool _retentionAutomaticCompactionEnabled;
    [ObservableProperty] private int _selectedRetentionAutomaticCompactionWindowHours = 24;
    [ObservableProperty] private string _retentionLastRunText = "";
    [ObservableProperty] private string _retentionLastStatusText = "";
    [ObservableProperty] private bool _retentionRulesAcknowledged;
    [ObservableProperty] private RepositoryRetentionRunResultDto? _retentionPreview;

    [ObservableProperty] private string _syncConflictStrategy = RepositorySyncConflictStrategies.LastWriteWins;
    [ObservableProperty] private string _syncRetryMaxAttempts = "5";
    [ObservableProperty] private string _syncRetryBaseDelaySeconds = "30";
    [ObservableProperty] private string _cloudSyncStatusText = "";
    [ObservableProperty] private string _cloudSyncLastSyncText = "";
    [ObservableProperty] private string _cloudSyncQueueText = "";
    [ObservableProperty] private string _cloudSyncErrorText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncLocalStorageText))]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageSavingsText))]
    [NotifyPropertyChangedFor(nameof(HasSyncCloudStorageSavingsText))]
    private long _localRepositoryTotalSizeBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageText))]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageHintText))]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageSavingsText))]
    [NotifyPropertyChangedFor(nameof(HasSyncCloudStorageSavingsText))]
    private long? _cloudRepositorySnapshotTotalSizeBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageText))]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageHintText))]
    [NotifyPropertyChangedFor(nameof(SyncCloudStorageSavingsText))]
    [NotifyPropertyChangedFor(nameof(HasSyncCloudStorageSavingsText))]
    private string _cloudRepositoryStorageState = "local_only";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudSyncSectionHint))]
    [NotifyPropertyChangedFor(nameof(ShowCloudConnectivityHint))]
    [NotifyPropertyChangedFor(nameof(CloudConnectivityHintText))]
    [NotifyPropertyChangedFor(nameof(ShowGuestCloudHint))]
    private bool _hasCloudAccess;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionSectionChevron))]
    private bool _isRetentionSectionExpanded;
    [ObservableProperty] private string _retentionProgressText = string.Empty;
    [ObservableProperty] private string _retentionResultText = string.Empty;
    [ObservableProperty] private double _retentionProgressValue;
    [ObservableProperty] private bool _isRetentionProgressIndeterminate;
    [ObservableProperty] private int _selectedRetentionTriggerPresetIndex;
    [ObservableProperty] private bool _isTransientActionBusy;
    [ObservableProperty] private string _transientActionTitle = string.Empty;
    [ObservableProperty] private string _transientActionDetail = string.Empty;
    [ObservableProperty] private bool _hasUnsavedSettingsChanges;

    public ObservableCollection<string> SelectedFormats { get; } = [];
    public ObservableCollection<string> AvailableFormats { get; } = [];
    public ObservableCollection<string> ExcludedPatterns { get; } = [];
    public ObservableCollection<FormatCategoryItemViewModel> FormatCategories { get; } = [];
    public ObservableCollection<int> RetentionMaintenanceHourOptions { get; } = new(Enumerable.Range(0, 24));
    public ObservableCollection<int> RetentionAutomaticCompactionWindowHourOptions { get; } = [6, 12, 24, 48, 72, 168];
    public ObservableCollection<string> SyncConflictStrategies { get; } =
    [
        RepositorySyncConflictStrategies.LastWriteWins,
        RepositorySyncConflictStrategies.ManualMerge,
        RepositorySyncConflictStrategies.PreserveBoth
    ];

    public RepositorySettingsViewModel(
        IServiceScopeExecutor scopeExecutor,
        IWindowService windows,
        IRepositoryCloudSyncOrchestrator cloudSync,
        ICloudSyncService cloudSyncService,
        IConnectivityStatusService connectivity,
        ICloudSyncRuntimeControlService cloudSyncRuntime,
        IAccessTokenPolicyService tokenPolicy,
        IUserProfileRepository userProfiles,
        ISensitiveActionGuard sensitiveActionGuard,
        ILogger<RepositorySettingsViewModel> log)
    {
        _scopeExecutor = scopeExecutor;
        _windows = windows;
        _cloudSync = cloudSync;
        _cloudSyncService = cloudSyncService;
        _connectivity = connectivity;
        _cloudSyncRuntime = cloudSyncRuntime;
        _tokenPolicy = tokenPolicy;
        _userProfiles = userProfiles;
        _sensitiveActionGuard = sensitiveActionGuard;
        _log = log;
        SelectedFormats.CollectionChanged += OnSelectedFormatsCollectionChanged;
        ExcludedPatterns.CollectionChanged += OnSettingsCollectionChanged;
        BuildFormatCategories();
        _localization.LanguageChanged += OnLanguageChanged;
        _experience.ModeChanged += OnExperienceModeChanged;
        _connectivity.StatusChanged += OnConnectivityStatusChanged;
        _cloudSyncRuntime.StateChanged += OnCloudSyncRuntimeStateChanged;
        RefreshLocalizationState();
    }

    public bool IsBasicMode => _experience.IsBasicMode;
    public bool IsProfessionalMode => _experience.IsProfessionalMode;
    public bool ShowAdvancedSyncSettings => IsProfessionalMode;
    public bool ShowAdvancedRetentionSettings => IsProfessionalMode;
    public bool CanSave => RepositoryId > 0 && !IsLoading && (HasUnsavedSettingsChanges || HasPendingCustomFormat);
    public string CloudSyncSectionHint => !HasCloudAccess
        ? Loc.T("repo_settings.sync_guest_hint")
        : IsBasicMode
            ? Loc.T("repo_settings.sync_hint_basic")
            : Loc.T("repo_settings.sync_hint");
    public string CloudSyncStatusLabel => IsBasicMode
        ? Loc.T("repo_settings.cloud_copy_status")
        : Loc.T("repo_settings.sync_status");
    public string CloudSyncQueueLabel => IsBasicMode
        ? Loc.T("repo_settings.sync_queue_status")
        : Loc.T("repo_settings.queue");
    public string SyncNowLabel => IsBasicMode
        ? Loc.T("repo_settings.sync_now_basic")
        : Loc.T("repo_settings.sync_now");
    public string CloudRepairTitle => IsBasicMode
        ? Loc.T("repo_settings.cloud_repair_title_basic")
        : Loc.T("repo_settings.cloud_repair_title");
    public string CloudRepairButtonLabel => IsBasicMode
        ? Loc.T("repo_settings.cloud_repair_button_basic")
        : Loc.T("repo_settings.cloud_repair_button");
    public string CloudRepairHint => IsBasicMode
        ? Loc.T("repo_settings.cloud_repair_hint_basic")
        : Loc.T("repo_settings.cloud_repair_hint");
    public string CloudGuestHint => Loc.T("app_settings.guest_auth_hint");
    public bool ShowCloudConnectivityHint => HasCloudAccess && ResolveCloudConnectivityMessage() is not null;
    public string CloudConnectivityHintText => ResolveCloudConnectivityMessage() ?? string.Empty;
    public string RetentionSectionHint => IsBasicMode
        ? Loc.T("repo_settings.retention_hint_basic")
        : Loc.T("repo_settings.retention_hint");
    public string RetentionPolicyTitle => IsBasicMode
        ? Loc.T("repo_settings.retention_policy_basic")
        : Loc.T("repo_settings.retention_policy");
    public bool CanEditLocalRetentionPolicy => RetentionUseLocalPolicy;
    public string RetentionEffectiveSourceText
    {
        get
        {
            if (!RetentionUseLocalPolicy)
                return Loc.T("repo_settings.retention_source_inherited_runtime");

            return RepositoryRetentionPolicySources.Normalize(RetentionPolicySource) switch
            {
                RepositoryRetentionPolicySources.Global => Loc.T("repo_settings.retention_source_global"),
                RepositoryRetentionPolicySources.ParentRepository => Loc.T("repo_settings.retention_source_parent"),
                RepositoryRetentionPolicySources.NestedRepositoryOverride => Loc.T("repo_settings.retention_source_nested_override"),
                RepositoryRetentionPolicySources.Repository => Loc.T("repo_settings.retention_source_repository"),
                _ => Loc.T("repo_settings.retention_source_none")
            };
        }
    }
    public string RetentionInheritanceHintText => RetentionUseLocalPolicy
        ? Loc.T("repo_settings.retention_local_override_hint")
        : Loc.T("repo_settings.retention_inherited_hint");
    public bool ShowCloudSyncProgressCard => HasCloudAccess && (HasActiveCloudSyncWork(_lastAppliedCloudSyncStatus) || IsSyncNowRunning);
    public bool HasMeasuredCloudSyncProgress => (_lastAppliedCloudSyncStatus?.UploadProgressTotal ?? 0) > 0;
    public bool ShowCloudSyncProgressPercent => HasMeasuredCloudSyncProgress;
    public bool CloudSyncProgressIsIndeterminate => !HasMeasuredCloudSyncProgress;
    public double CloudSyncProgressValue => CalculateProgressPercent(
        _lastAppliedCloudSyncStatus?.UploadProgressCurrent ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressTotal ?? 0);
    public string CloudSyncProgressPhaseText => FormatCloudSyncProgressPhase(_lastAppliedCloudSyncStatus?.LastStatus);
    public string CloudSyncProgressPercentText => HasMeasuredCloudSyncProgress
        ? $"{Math.Clamp((int)Math.Round(CloudSyncProgressValue), 0, 100)}%"
        : string.Empty;
    public string CloudSyncProgressSummaryText => FormatCloudSyncProgressSummary(
        _lastAppliedCloudSyncStatus?.LastStatus,
        _lastAppliedCloudSyncStatus?.UploadProgressCurrent ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressTotal ?? 0);
    public string CloudSyncProgressEtaText => FormatEtaText(
        _lastAppliedCloudSyncStatus?.UploadProgressCurrent ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressTotal ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressStartedAtUtc,
        _lastAppliedCloudSyncStatus?.UploadProgressUpdatedAtUtc);
    public string CloudSyncProgressElapsedText => FormatElapsedText(
        _lastAppliedCloudSyncStatus?.UploadProgressCurrent ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressTotal ?? 0,
        _lastAppliedCloudSyncStatus?.UploadProgressStartedAtUtc,
        _lastAppliedCloudSyncStatus?.UploadProgressUpdatedAtUtc);
    public string CloudSyncProgressLastUpdateText => FormatLastProgressUpdateText(
        _lastAppliedCloudSyncStatus?.UploadProgressUpdatedAtUtc);
    public string SyncLocalStorageText => FormatBytes(LocalRepositoryTotalSizeBytes);
    public string SyncCloudStorageText => CloudRepositoryStorageState switch
    {
        "loading" => Loc.T("repo_settings.sync_storage_loading"),
        "local_only" => Loc.T("repo_settings.sync_storage_local_only"),
        "auth_required" => Loc.T("repo_settings.sync_storage_auth_required"),
        "no_remote" => Loc.T("repo_settings.sync_storage_no_remote"),
        "unavailable" => Loc.T("common.not_available_short"),
        _ when CloudRepositorySnapshotTotalSizeBytes is > 0 => FormatBytes(CloudRepositorySnapshotTotalSizeBytes.Value),
        _ when CloudRepositorySnapshotTotalSizeBytes == 0 => FormatBytes(0),
        _ => Loc.T("common.not_available_short")
    };
    public string SyncCloudStorageHintText => CloudRepositoryStorageState switch
    {
        "loading" => Loc.T("repo_settings.sync_storage_cloud_loading_hint"),
        "local_only" => Loc.T("repo_settings.sync_storage_cloud_local_only_hint"),
        "auth_required" => Loc.T("repo_settings.sync_storage_cloud_auth_hint"),
        "no_remote" => Loc.T("repo_settings.sync_storage_cloud_no_remote_hint"),
        "unavailable" => Loc.T("repo_settings.sync_storage_cloud_unavailable_hint"),
        _ => HasCloudSnapshotSizeCloseToLocal
            ? Loc.T("repo_settings.sync_storage_cloud_latest_snapshot_hint_close_to_local")
            : Loc.T("repo_settings.sync_storage_cloud_latest_snapshot_hint")
    };
    public bool HasSyncCloudStorageSavingsText => !string.IsNullOrWhiteSpace(SyncCloudStorageSavingsText);
    public string SyncCloudStorageSavingsText
    {
        get
        {
            if (CloudRepositoryStorageState != "latest"
                || CloudRepositorySnapshotTotalSizeBytes is not > 0
                || LocalRepositoryTotalSizeBytes <= 0)
            {
                return string.Empty;
            }

            var local = Math.Max(1L, LocalRepositoryTotalSizeBytes);
            var uploaded = Math.Max(0L, CloudRepositorySnapshotTotalSizeBytes.Value);
            if (uploaded >= local)
                return Loc.T("repo_settings.sync_storage_cloud_savings_none");

            var savedBytes = local - uploaded;
            var savedPercent = Math.Clamp(savedBytes * 100d / local, 0d, 100d);
            if (savedPercent < 3d)
                return Loc.T("repo_settings.sync_storage_cloud_savings_none");

            return Loc.F(
                "repo_settings.sync_storage_cloud_savings_reduced",
                Math.Round(savedPercent),
                FormatBytes(savedBytes));
        }
    }
    public bool CanRunSyncNow => HasCloudAccess && !IsSyncNowRunning;
    public bool CanRunCloudRepair => HasCloudAccess && !IsCloudRepairRunning;
    public bool ShowGuestCloudHint => !HasCloudAccess;
    private bool HasCloudSnapshotSizeCloseToLocal
    {
        get
        {
            if (CloudRepositoryStorageState != "latest"
                || CloudRepositorySnapshotTotalSizeBytes is not > 0
                || LocalRepositoryTotalSizeBytes <= 0)
            {
                return false;
            }

            var local = (double)LocalRepositoryTotalSizeBytes;
            var remote = (double)CloudRepositorySnapshotTotalSizeBytes.Value;
            var deltaRatio = Math.Abs(local - remote) / Math.Max(local, remote);
            return deltaRatio <= 0.2d;
        }
    }

    public async Task LoadAsync(int repositoryId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            _log.LogInformation("Loading repository settings. RepositoryId {RepositoryId}", repositoryId);
            await Task.Yield();

            var repo = await SendMediatorAsync(new GetRepositoryDetailQuery(repositoryId));
            if (repo is null)
            {
                ErrorMessage = Loc.T("repo_settings.error_not_found");
                return;
            }

            var allFormats = await SendMediatorAsync(new GetTrackedExtensionsQuery());
            HasCloudAccess = (await _userProfiles.GetActiveProfileAsync()) is not null;

            RepositoryId = repo.Id;
            RepositoryName = repo.Name;
            Description = repo.Description;
            DirectoryPath = repo.DirectoryPath;
            AutoCaptureFileVersions = repo.AutoCaptureFileVersions;
            ProtectCloudMetadata = repo.ProtectCloudMetadata;
            LocalRepositoryTotalSizeBytes = Math.Max(0L, repo.TotalSizeBytes);

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

            ExcludedPatterns.Clear();
            foreach (var pattern in repo.ExcludedPatterns
                         .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static pattern => pattern, StringComparer.OrdinalIgnoreCase))
            {
                ExcludedPatterns.Add(pattern);
            }

            RefreshAvailableFormats();
            RefreshFormatCategoryState();

            ApplyRetentionPolicy(repo.RetentionPolicy);
            ApplyCloudSyncStatus(repo.CloudSync);
            ApplyCloudStorageFootprintState(repo.CloudSync);
            _ = RefreshCloudStorageFootprintAsync(repo.Id, repo.CloudSync);

            RetentionPreview = null;
            RetentionRulesAcknowledged = false;
            RetentionResultText = string.Empty;
            RetentionProgressText = string.Empty;
            BundleOperationMessage = string.Empty;
            _lastSavedSettingsSnapshot = BuildSettingsSnapshot();
            RefreshSettingsDirtyState();
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

        var local = StoragePathResolver.TryGetLocalPath(res.FirstOrDefault());
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
    private void ToggleFormatCategory(FormatCategoryItemViewModel? category)
    {
        if (category is null)
            return;

        category.IsApplied = !category.IsApplied;
        ApplyCategorySelection(category);
    }

    [RelayCommand]
    private void AddCustomExclusionPattern()
    {
        var normalized = NormalizeExclusionPattern(CustomExclusionPattern);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!ExcludedPatterns.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            ExcludedPatterns.Add(normalized);

        CustomExclusionPattern = string.Empty;
    }

    [RelayCommand]
    private async Task BrowseExclusionFolderAsync()
    {
        var repositoryRoot = NormalizeDirectoryPath(DirectoryPath);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            ErrorMessage = Loc.T("repo_settings.excluded_paths_outside_repository");
            return;
        }

        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var window = _windows.Create<RepositoryFolderPickerWindow>();
        if (window.DataContext is not RepositoryFolderPickerWindowViewModel vm)
        {
            ErrorMessage = Loc.T("repo_settings.error_picker_unavailable");
            return;
        }

        ErrorMessage = null;
        await vm.ConfigureAsync(repositoryRoot, CustomExclusionPattern);
        await _windows.ShowDialogAsync(window, owner);

        var relativePath = vm.DialogResultRelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = NormalizeExclusionPattern(relativePath);
        if (!ExcludedPatterns.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            ExcludedPatterns.Add(normalized);

        CustomExclusionPattern = normalized;
    }

    [RelayCommand]
    private void RemoveExclusionPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var match = ExcludedPatterns.FirstOrDefault(item =>
            item.Equals(pattern, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            ExcludedPatterns.Remove(match);
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

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ApplyPendingCustomFormat();

        if (!HasUnsavedSettingsChanges)
        {
            _log.LogInformation("Repository settings save skipped because nothing changed. RepositoryId {RepositoryId}", RepositoryId);
            return;
        }

        var saveTimer = Stopwatch.StartNew();
        var validationTimer = Stopwatch.StartNew();
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            _log.LogInformation("Saving repository settings. RepositoryId {RepositoryId}", RepositoryId);

            var policy = BuildRetentionPolicyFromState();
            var retentionValidationError = ValidateRetentionPolicyBeforeSave(policy);
            if (!string.IsNullOrWhiteSpace(retentionValidationError))
            {
                ErrorMessage = retentionValidationError;
                return;
            }

            if (!await EnsureManualCleanupConfirmationAsync(policy, applyingNow: false))
                return;
            var validationMs = validationTimer.ElapsedMilliseconds;

            var syncRetryAttempts = ParseIntOrDefault(SyncRetryMaxAttempts, 5, 1, 20);
            var syncRetryDelay = ParseIntOrDefault(SyncRetryBaseDelaySeconds, 30, 5, 600);
            var strategy = RepositorySyncConflictStrategies.Normalize(SyncConflictStrategy);
            var beforeSaveSnapshot = _lastSavedSettingsSnapshot;
            var currentSnapshot = BuildSettingsSnapshot(policy, strategy, syncRetryAttempts, syncRetryDelay);
            var cloudSettingsChanged = beforeSaveSnapshot is not null && currentSnapshot.CloudKey != beforeSaveSnapshot.CloudKey;
            var mediatorTimer = Stopwatch.StartNew();

            var result = await SendMediatorAsync(new UpdateRepositoryConfigurationCommand(
                RepositoryId,
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                AutoCaptureFileVersions,
                ProtectCloudMetadata,
                ExcludedPatterns.ToList(),
                policy,
                strategy,
                syncRetryAttempts,
                syncRetryDelay));
            var mediatorMs = mediatorTimer.ElapsedMilliseconds;

            if (!result.Success)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.error_save_failed");
                return;
            }

            if (cloudSettingsChanged)
            {
                var cloudTimer = Stopwatch.StartNew();
                try
                {
                    await _cloudSync.ProcessPendingQueueAsync();
                }
                catch (Exception syncEx)
                {
                    _log.LogWarning(syncEx, "Cloud queue resume skipped after repository settings save. RepositoryId {RepositoryId}", RepositoryId);
                }

                _log.LogInformation(
                    "Repository settings cloud queue refresh completed. RepositoryId {RepositoryId}. DurationMs {DurationMs}",
                    RepositoryId,
                    cloudTimer.ElapsedMilliseconds);
            }

            if (RepositoryUpdated is not null)
                await RepositoryUpdated.Invoke(RepositoryId);

            _lastAppliedRetentionPolicy = policy;
            _lastSavedSettingsSnapshot = currentSnapshot;
            RefreshSettingsDirtyState();
            _log.LogInformation(
                "Repository settings saved. RepositoryId {RepositoryId}. ValidationMs {ValidationMs}. MediatorMs {MediatorMs}. TotalMs {TotalMs}. CloudSettingsChanged {CloudSettingsChanged}",
                RepositoryId,
                validationMs,
                mediatorMs,
                saveTimer.ElapsedMilliseconds,
                cloudSettingsChanged);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save repository settings for {RepositoryId}", RepositoryId);
            ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_settings.error_save_failed");
        }
        finally
        {
            IsLoading = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (RepositoryId <= 0 || IsSyncNowRunning)
            return;

        if (!HasCloudAccess)
        {
            ErrorMessage = CloudGuestHint;
            return;
        }

        if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
        {
            ErrorMessage = connectivityMessage;
            return;
        }

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_cloud_sync_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncNowRunning = true;
            ErrorMessage = null;
            _log.LogInformation("Repository cloud sync requested. RepositoryId {RepositoryId}", RepositoryId);
            _ = RunSyncNowInBackgroundAsync(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run cloud sync for repository {RepositoryId}", RepositoryId);
            ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_settings.error_sync_failed");
        }
    }

    private async Task RunSyncNowInBackgroundAsync(int repositoryId)
    {
        try
        {
            await Task.Yield();
            await _cloudSync.TryPushLatestSnapshotAsync(repositoryId);

            if (RepositoryId == repositoryId)
                await RunOnUiAsync(() => LoadAsync(repositoryId));

            _log.LogInformation("Repository cloud sync finished. RepositoryId {RepositoryId}", repositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run cloud sync for repository {RepositoryId}", repositoryId);
            await RunOnUiAsync(() =>
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_settings.error_sync_failed");
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                IsSyncNowRunning = false;
            });
        }
    }

    [RelayCommand]
    private async Task OpenCloudRepositoryManagerAsync()
    {
        var owner = _windows.GetActiveWindow();
        var window = _windows.Create<CloudRepositoryManagerWindow>();

        if (window.DataContext is CloudRepositoryManagerWindowViewModel vm)
            await vm.RefreshAsync();

        if (owner is not null)
            await _windows.ShowDialogAsync(window, owner);
        else
            _windows.Show(window);
    }

    [RelayCommand]
    private async Task RepairCloudDataAsync()
    {
        if (RepositoryId <= 0 || IsCloudRepairRunning)
            return;

        if (!HasCloudAccess)
        {
            ErrorMessage = CloudGuestHint;
            return;
        }

        if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
        {
            ErrorMessage = connectivityMessage;
            CloudRepairMessage = connectivityMessage;
            return;
        }

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_repository_cloud_repair",
                "security.action_repository_cloud_repair_body",
                [RepositoryName]);

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
            _ = RunCloudRepairInBackgroundAsync(RepositoryId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to repair cloud data for repository {RepositoryId}", RepositoryId);
            ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_settings.cloud_repair_failed");
            CloudRepairMessage = ErrorMessage;
        }
    }

    private async Task RunCloudRepairInBackgroundAsync(int repositoryId)
    {
        try
        {
            await Task.Yield();

            var result = await _cloudSync.RepairRepositoryCloudDataAsync(repositoryId);
            if (RepositoryId == repositoryId)
                await RunOnUiAsync(() => LoadAsync(repositoryId));

            await RunOnUiAsync(() =>
            {
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
                    ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.ErrorMessage, "repo_settings.cloud_repair_failed");
            });

            _log.LogInformation(
                "Repository cloud repair finished. RepositoryId {RepositoryId}. Success {Success}. ReferencedBlocks {ReferencedBlocks}. UploadedBlocks {UploadedBlocks}. MissingLocalBlocks {MissingLocalBlocks}. FailedUploads {FailedUploads}",
                repositoryId,
                result.Success,
                result.ReferencedBlocks,
                result.UploadedBlocks,
                result.MissingLocalBlocks,
                result.FailedUploads);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to repair cloud data for repository {RepositoryId}", repositoryId);
            await RunOnUiAsync(() =>
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "repo_settings.cloud_repair_failed");
                CloudRepairMessage = ErrorMessage;
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                IsCloudRepairRunning = false;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleOperations))]
    private async Task ExportBundleAsync()
    {
        if (!CanRunBundleOperations)
            return;

        await RunTransientActionAsync(
            "repo_settings.bundle_opening_title",
            "repo_settings.bundle_opening_detail",
            async () =>
            {
                var owner = _windows.GetActiveWindow();
                if (owner is null)
                {
                    ErrorMessage = Loc.T("repo_settings.error_picker_unavailable");
                    return;
                }

                var window = _windows.Create<RepositoryBundleExportWizardWindow>();
                if (window.DataContext is RepositoryBundleExportWizardWindowViewModel vm)
                    vm.Configure(RepositoryId, RepositoryName);

                await _windows.ShowDialogAsync(window, owner);

                if (window.IsCompleted)
                {
                    ErrorMessage = null;
                    BundleOperationMessage = window.ResultSummary;
                }
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open export bundle wizard for repository {RepositoryId}", RepositoryId);
                ErrorMessage = Loc.T("repo_settings.error_bundle_failed");
            });
    }

    [RelayCommand(CanExecute = nameof(CanRunBundleOperations))]
    private async Task ImportBundleAsync()
    {
        if (!CanRunBundleOperations)
            return;

        await RunTransientActionAsync(
            "repo_settings.bundle_opening_title",
            "repo_settings.bundle_opening_detail",
            async () =>
            {
                var owner = _windows.GetActiveWindow();
                if (owner is null)
                {
                    ErrorMessage = Loc.T("repo_settings.error_picker_unavailable");
                    return;
                }

                var window = _windows.Create<RepositoryBundleImportWizardWindow>();
                if (window.DataContext is RepositoryBundleImportWizardWindowViewModel vm)
                    vm.Configure(DirectoryPath);

                await _windows.ShowDialogAsync(window, owner);

                if (window.IsCompleted)
                {
                    ErrorMessage = null;
                    BundleOperationMessage = window.ResultSummary;
                }
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open import bundle wizard for repository {RepositoryId}", RepositoryId);
                ErrorMessage = Loc.T("repo_settings.error_bundle_failed");
            });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            if (!await ConfirmRepositoryDeletionAsync())
                return;

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_delete_repository",
                "security.action_delete_repository_body",
                [RepositoryName]);

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ErrorMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            await SendMediatorAsync(new DeleteRepositoryCommand(RepositoryId));
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

    [RelayCommand]
    private void ToggleRetentionSection() => IsRetentionSectionExpanded = !IsRetentionSectionExpanded;

    [RelayCommand(CanExecute = nameof(CanRunRetentionDryRun))]
    private Task RunRetentionDryRunAsync() => RunRetentionAsync(dryRun: true);

    [RelayCommand]
    private async Task OpenRetentionSetupAsync()
    {
        if (!CanOpenRetentionSetup)
            return;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var window = _windows.Create<RepositoryRetentionWizardWindow>();
        if (window.DataContext is not RepositoryRetentionWizardWindowViewModel vm)
            return;

        vm.Configure(RepositoryId, RepositoryName, BuildRetentionPolicyFromState());
        await _windows.ShowDialogAsync(window, owner);

        if (!vm.IsSuccessful || vm.ResultPolicy is null)
            return;

        ApplyRetentionEditableState(vm.ResultPolicy);
        RetentionPreview = vm.PreviewResult;
        RetentionRulesAcknowledged = vm.RulesAcknowledged;
        RetentionResultText = vm.PreviewResult?.Summary ?? string.Empty;
    }

    private void OnSelectedFormatsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshAvailableFormats();
        RefreshFormatCategoryState();
        RefreshSettingsDirtyState();
    }

    private void OnSettingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshSettingsDirtyState();

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

    private void BuildFormatCategories()
    {
        FormatCategories.Clear();
        foreach (var definition in TrackedFormatCategoryCatalog.All)
            FormatCategories.Add(new FormatCategoryItemViewModel(definition));
    }

    private void ApplyCategorySelection(FormatCategoryItemViewModel category)
    {
        if (category.IsApplied)
        {
            foreach (var format in category.Formats)
                AddAvailableFormat(format);

            return;
        }

        var protectedFormats = FormatCategories
            .Where(item => !ReferenceEquals(item, category) && item.IsApplied)
            .SelectMany(item => item.Formats)
            .Select(NormalizeFormat)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var format in category.Formats.Select(NormalizeFormat))
        {
            if (protectedFormats.Contains(format))
                continue;

            RemoveFormat(format);
        }
    }

    private void RefreshFormatCategoryState()
    {
        var selected = SelectedFormats
            .Select(NormalizeFormat)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var category in FormatCategories)
        {
            var shouldBeApplied = category.Formats
                .Select(NormalizeFormat)
                .All(selected.Contains);

            if (category.IsApplied != shouldBeApplied)
                category.IsApplied = shouldBeApplied;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRetentionApply))]
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
    public bool CanRunBundleOperations => RepositoryId > 0 && !IsLoading && !IsBundleOperationRunning && !IsTransientActionBusy;
    public bool ShowBlockingOverlay => IsLoading || IsTransientActionBusy;
    public string BlockingOverlayTitle => IsTransientActionBusy
        ? TransientActionTitle
        : Loc.T("repo_settings.loading_title");
    public string BlockingOverlayDetail => IsTransientActionBusy
        ? TransientActionDetail
        : Loc.T("repo_settings.loading_detail");

    public bool CanOpenRetentionSetup => RepositoryId > 0 && !IsLoading && !IsRetentionRunning;
    public bool CanRunRetention => CanRunRetentionDryRun;
    public bool CanRunRetentionDryRun =>
        RepositoryId > 0
        && (RetentionEnabled || !RetentionUseLocalPolicy)
        && !IsRetentionRunning
        && !RequiresManualCleanupUnlock
        && (!RetentionUseLocalPolicy || IsRetentionAutomaticCompactionConfigured);
    public bool CanRunRetentionApply =>
        CanRunRetentionDryRun
        && !RequiresManualCleanupUnlock
        && (!RetentionUseLocalPolicy || IsRetentionAutomaticCompactionConfigured);
    public bool CanCancelRetention => IsRetentionRunning;
    public bool HasRetentionPreview => RetentionPreview is not null;
    public bool RequiresManualCleanupUnlock => RetentionUseLocalPolicy && RetentionEnabled && TargetsManualSnapshotsFromState() && !RetentionManualCleanupAllowed;
    public bool ShowsRetentionManualRiskBadge => RetentionEnabled && TargetsManualSnapshotsFromState();
    public bool IsRetentionAutomaticCompactionConfigured => !RetentionAutomaticCompactionEnabled || SelectedRetentionAutomaticCompactionWindowHours > 0;
    public bool IsRetentionMaintenanceWindowConfigured =>
        !RetentionEnabled
        || (RetentionMaintenanceWindowEnabled
            && SelectedRetentionMaintenanceWindowStartHour != SelectedRetentionMaintenanceWindowEndHour);
    public string RetentionSectionChevron => IsRetentionSectionExpanded ? "▼" : "▶";
    public string RetentionStorageModeSummaryText => RetentionArchiveMode
        ? Loc.T("repo_settings.retention_storage_mode_archive_summary")
        : Loc.T("repo_settings.retention_storage_mode_delete_summary");
    public string RetentionManualRiskBadgeText =>
        !ShowsRetentionManualRiskBadge
            ? Loc.T("repo_settings.retention_manual_badge_safe")
            : RequiresManualCleanupUnlock
                ? Loc.T("repo_settings.retention_manual_badge_locked")
                : Loc.T("repo_settings.retention_manual_badge_enabled");
    public string RetentionHowItWorksText => BuildRetentionHowItWorksText();
    public string RetentionAutomaticCompactionSummaryText =>
        !RetentionAutomaticCompactionEnabled
            ? Loc.T("repo_settings.retention_compaction_disabled")
            : Loc.F("repo_settings.retention_compaction_summary", SelectedRetentionAutomaticCompactionWindowHours);
    public string RetentionMaintenanceWindowSummaryText =>
        !RetentionMaintenanceWindowEnabled
            ? Loc.T("repo_settings.retention_window_disabled")
            : SelectedRetentionMaintenanceWindowStartHour == SelectedRetentionMaintenanceWindowEndHour
                ? Loc.T("repo_settings.retention_window_required")
                : Loc.F(
                    "repo_settings.retention_window_summary",
                    FormatHourLabel(SelectedRetentionMaintenanceWindowStartHour),
                    FormatHourLabel(SelectedRetentionMaintenanceWindowEndHour));
    public string RetentionPreviewStateText
    {
        get
        {
            if (RetentionPreview is null)
                return Loc.T("repo_settings.retention_preview_needed");

            return RetentionPreview.PolicyApplied
                ? Loc.T("repo_settings.retention_preview_changes_found")
                : Loc.T("repo_settings.retention_preview_no_changes");
        }
    }
    public string RetentionPreviewSnapshotsText => (RetentionPreview?.SnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewAutomaticSnapshotsText => (RetentionPreview?.AutomaticSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewAutomaticBreakdownText => FormatRetentionKindBreakdown(RetentionPreview?.AutomaticSnapshotsMarked ?? 0);
    public string RetentionPreviewManualSnapshotsText => (RetentionPreview?.ManualSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewManualBreakdownText => FormatRetentionKindBreakdown(RetentionPreview?.ManualSnapshotsMarked ?? 0);
    public string RetentionPreviewWorkingSnapshotsText => (RetentionPreview?.WorkingSnapshotsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewWorkingBreakdownText => FormatRetentionKindBreakdown(RetentionPreview?.WorkingSnapshotsMarked ?? 0);
    public string RetentionPreviewAutomaticCompactionText => (RetentionPreview?.AutomaticSnapshotsCompacted ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewVersionsText => (RetentionPreview?.FileVersionsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewDiffsText => (RetentionPreview?.DiffsMarked ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewBlocksText => (RetentionPreview?.BlockFilesDeleted ?? 0).ToString(CultureInfo.InvariantCulture);
    public string RetentionPreviewFreedSpaceText => FormatSize(RetentionPreview?.EstimatedFreedBytes ?? 0);
    public string RetentionPreviewSummaryText => RetentionPreview?.Summary ?? string.Empty;
    public string RetentionPreviewImpactText => BuildRetentionPreviewImpactText(RetentionPreview);
    public string RetentionPrerequisitesText
    {
        get
        {
            if (!RetentionUseLocalPolicy)
            {
                if (!HasRetentionPreview)
                    return Loc.T("repo_settings.retention_preview_optional");

                return (RetentionPreview?.PolicyApplied ?? false)
                    ? Loc.T("repo_settings.retention_apply_ready")
                    : Loc.T("repo_settings.retention_preview_ready_no_cleanup");
            }

            if (!RetentionEnabled)
                return Loc.T("repo_settings.retention_preview_disabled");
            if (RequiresManualCleanupUnlock)
                return Loc.T("repo_settings.retention_manual_cleanup_required");
            if (!IsRetentionAutomaticCompactionConfigured)
                return Loc.T("repo_settings.retention_compaction_window_required");
            if (!HasRetentionPreview)
                return Loc.T("repo_settings.retention_preview_optional");
            if (!(RetentionPreview?.PolicyApplied ?? false))
                return Loc.T("repo_settings.retention_preview_ready_no_cleanup");
            if (NeedsManualCleanupConfirmation(BuildRetentionPolicyFromState()))
                return Loc.T("repo_settings.retention_manual_second_confirm_required");

            return Loc.T("repo_settings.retention_apply_ready");
        }
    }

    partial void OnIsRetentionRunningChanged(bool value)
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        CancelRetentionCommand.NotifyCanExecuteChanged();
        OpenRetentionSetupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenRetentionSetup));
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanRunRetentionDryRun));
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(CanCancelRetention));
        OnPropertyChanged(nameof(RetentionPrerequisitesText));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OpenRetentionSetupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenRetentionSetup));
        OnPropertyChanged(nameof(CanRunBundleOperations));
        OnPropertyChanged(nameof(ShowBlockingOverlay));
        OnPropertyChanged(nameof(BlockingOverlayTitle));
        OnPropertyChanged(nameof(BlockingOverlayDetail));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnHasUnsavedSettingsChangesChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnIsBundleOperationRunningChanged(bool value)
    {
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunBundleOperations));
    }

    partial void OnIsTransientActionBusyChanged(bool value)
    {
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunBundleOperations));
        OnPropertyChanged(nameof(ShowBlockingOverlay));
        OnPropertyChanged(nameof(BlockingOverlayTitle));
        OnPropertyChanged(nameof(BlockingOverlayDetail));
    }

    partial void OnRepositoryIdChanged(int value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OpenRetentionSetupCommand.NotifyCanExecuteChanged();
        ExportBundleCommand.NotifyCanExecuteChanged();
        ImportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpenRetentionSetup));
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanRunRetentionDryRun));
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(CanRunBundleOperations));
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnRepositoryNameChanged(string value) => RefreshSettingsDirtyState();

    partial void OnDescriptionChanged(string? value) => RefreshSettingsDirtyState();

    partial void OnDirectoryPathChanged(string value) => RefreshSettingsDirtyState();

    partial void OnCustomFormatChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
    }

    partial void OnAutoCaptureFileVersionsChanged(bool value) => RefreshSettingsDirtyState();

    partial void OnProtectCloudMetadataChanged(bool value) => RefreshSettingsDirtyState();

    partial void OnSyncConflictStrategyChanged(string value) => RefreshSettingsDirtyState();

    partial void OnSyncRetryMaxAttemptsChanged(string value) => RefreshSettingsDirtyState();

    partial void OnSyncRetryBaseDelaySecondsChanged(string value) => RefreshSettingsDirtyState();

    partial void OnRetentionEnabledChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(RetentionTriggerFilter))
            RetentionTriggerFilter = SafeDefaultRetentionTriggerFilter;

        if (value && !RetentionIncludesAutomaticSnapshots && !RetentionIncludesManualSnapshots && !RetentionIncludesWorkingSnapshots)
            RetentionIncludesAutomaticSnapshots = true;

        InvalidateRetentionPreview();
        RunRetentionCommandStateRefresh();
        RefreshSettingsDirtyState();
    }

    partial void OnRetentionUseLocalPolicyChanged(bool value)
    {
        RetentionPolicySource = value
            ? RepositoryRetentionPolicySources.Repository
            : RepositoryRetentionPolicySources.None;
        InvalidateRetentionPreview();
        RunRetentionCommandStateRefresh();
        RefreshSettingsDirtyState();
    }

    partial void OnRetentionMaxAgeDaysChanged(string value) => OnRetentionPolicyEdited();

    partial void OnRetentionMaxSnapshotsChanged(string value) => OnRetentionPolicyEdited();

    partial void OnRetentionMaxTotalSizeMbChanged(string value) => OnRetentionPolicyEdited();

    partial void OnRetentionTriggerFilterChanged(string value)
    {
        SyncRetentionTriggerSelectionFromFilter(value);
        OnRetentionPolicyEdited();
    }

    partial void OnRetentionIncludesAutomaticSnapshotsChanged(bool value) => SyncRetentionTriggerFilterFromSelection();
    partial void OnRetentionIncludesManualSnapshotsChanged(bool value) => SyncRetentionTriggerFilterFromSelection();
    partial void OnRetentionIncludesWorkingSnapshotsChanged(bool value) => SyncRetentionTriggerFilterFromSelection();
    partial void OnSelectedRetentionTriggerPresetIndexChanged(int value) => ApplyRetentionTriggerPresetFromIndex(value);

    partial void OnRetentionRunIntervalMinutesChanged(int value) => OnRetentionPolicyEdited();

    partial void OnRetentionMaintenanceWindowEnabledChanged(bool value)
    {
        if (value && SelectedRetentionMaintenanceWindowStartHour == SelectedRetentionMaintenanceWindowEndHour)
            SelectedRetentionMaintenanceWindowEndHour = (SelectedRetentionMaintenanceWindowStartHour + 4) % 24;

        OnRetentionPolicyEdited();
    }

    partial void OnSelectedRetentionMaintenanceWindowStartHourChanged(int value)
        => OnRetentionPolicyEdited();

    partial void OnSelectedRetentionMaintenanceWindowEndHourChanged(int value)
        => OnRetentionPolicyEdited();

    partial void OnRetentionArchiveModeChanged(bool value)
        => OnRetentionPolicyEdited();

    partial void OnRetentionManualHistorySafeModeChanged(bool value)
    {
        if (_syncingManualHistorySafeMode)
            return;

        if (value)
            ApplyManualHistorySafeMode();
        else
            OnRetentionPolicyEdited();
    }

    partial void OnRetentionManualCleanupAllowedChanged(bool value)
        => OnRetentionPolicyEdited();

    partial void OnRetentionAutomaticCompactionEnabledChanged(bool value)
    {
        if (value && SelectedRetentionAutomaticCompactionWindowHours <= 0)
            SelectedRetentionAutomaticCompactionWindowHours = 24;

        OnRetentionPolicyEdited();
    }

    partial void OnSelectedRetentionAutomaticCompactionWindowHoursChanged(int value)
        => OnRetentionPolicyEdited();

    partial void OnRetentionRulesAcknowledgedChanged(bool value)
    {
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(RetentionPrerequisitesText));
    }

    partial void OnRetentionPreviewChanged(RepositoryRetentionRunResultDto? value)
    {
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRetentionPreview));
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(RetentionPreviewStateText));
        OnPropertyChanged(nameof(RetentionPreviewSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewManualSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewManualBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewWorkingSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewWorkingBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticCompactionText));
        OnPropertyChanged(nameof(RetentionPreviewVersionsText));
        OnPropertyChanged(nameof(RetentionPreviewDiffsText));
        OnPropertyChanged(nameof(RetentionPreviewBlocksText));
        OnPropertyChanged(nameof(RetentionPreviewFreedSpaceText));
        OnPropertyChanged(nameof(RetentionPreviewSummaryText));
        OnPropertyChanged(nameof(RetentionPreviewImpactText));
        OnPropertyChanged(nameof(RetentionPrerequisitesText));
    }

    partial void OnHasCloudAccessChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunSyncNow));
        OnPropertyChanged(nameof(CanRunCloudRepair));
        OnPropertyChanged(nameof(ShowGuestCloudHint));
    }

    partial void OnIsSyncNowRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunSyncNow));
        NotifyCloudSyncPresentationChanged();
        UpdateCloudSyncStatusAutoRefreshState();
    }

    partial void OnIsCloudRepairRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunCloudRepair));
    }

    private void OnRetentionPolicyEdited()
    {
        SyncManualHistorySafeModeFromState();
        InvalidateRetentionPreview();
        RunRetentionCommandStateRefresh();
        RefreshSettingsDirtyState();
    }

    private void InvalidateRetentionPreview()
    {
        RetentionPreview = null;
        RetentionRulesAcknowledged = false;
        _manualRetentionCleanupConfirmedForCurrentPolicy = false;
    }

    private void RunRetentionCommandStateRefresh()
    {
        RunRetentionDryRunCommand.NotifyCanExecuteChanged();
        RunRetentionApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanRunRetentionDryRun));
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(RequiresManualCleanupUnlock));
        OnPropertyChanged(nameof(ShowsRetentionManualRiskBadge));
        OnPropertyChanged(nameof(RetentionManualRiskBadgeText));
        OnPropertyChanged(nameof(RetentionHowItWorksText));
        OnPropertyChanged(nameof(IsRetentionAutomaticCompactionConfigured));
        OnPropertyChanged(nameof(RetentionAutomaticCompactionSummaryText));
        OnPropertyChanged(nameof(IsRetentionMaintenanceWindowConfigured));
        OnPropertyChanged(nameof(RetentionMaintenanceWindowSummaryText));
        OnPropertyChanged(nameof(RetentionStorageModeSummaryText));
        OnPropertyChanged(nameof(RetentionPrerequisitesText));
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
                var message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.error_bundle_failed");
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

    private async Task RunTransientActionAsync(
        string titleKey,
        string detailKey,
        Func<Task> action,
        Action<Exception> onError)
    {
        if (IsTransientActionBusy)
            return;

        try
        {
            IsTransientActionBusy = true;
            TransientActionTitle = Loc.T(titleKey);
            TransientActionDetail = Loc.T(detailKey);
            await action();
        }
        catch (Exception ex)
        {
            onError(ex);
        }
        finally
        {
            IsTransientActionBusy = false;
            TransientActionTitle = string.Empty;
            TransientActionDetail = string.Empty;
        }
    }

    private async Task RunRetentionAsync(bool dryRun)
    {
        if (dryRun)
        {
            if (!CanRunRetentionDryRun)
                return;
        }
        else if (!CanRunRetentionApply)
        {
            return;
        }

        try
        {
            var currentPolicy = BuildRetentionPolicyFromState();
            var hasUnsavedRetentionPolicyChanges = HasRetentionPolicyChanges(currentPolicy);

            if (!dryRun
                && currentPolicy.HasLocalOverride
                && !await EnsureManualCleanupConfirmationAsync(currentPolicy, applyingNow: true))
            {
                return;
            }

            IsRetentionRunning = true;
            if (dryRun)
                RetentionPreview = null;

            RetentionResultText = string.Empty;
            RetentionProgressText = Loc.T("repo_settings.retention_running");
            RetentionProgressValue = 0;
            IsRetentionProgressIndeterminate = true;
            ErrorMessage = null;

            _retentionCts?.Dispose();
            _retentionCts = new CancellationTokenSource();

            var progress = new Progress<RepositoryRetentionProgressDto>(p =>
            {
                RetentionProgressText = $"{p.Percent}% {p.Message}";
                RetentionProgressValue = Math.Clamp(p.Percent, 0, 100);
                IsRetentionProgressIndeterminate = false;
            });

            var result = await SendMediatorAsync(
                new RunRepositoryRetentionCommand(
                    RepositoryId,
                    dryRun,
                    currentPolicy.HasLocalOverride ? currentPolicy : null,
                    progress),
                _retentionCts.Token);

            if (!result.Success || result.Value is null)
            {
                RetentionResultText = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.retention_failed");
                return;
            }

            if (dryRun)
            {
                RetentionPreview = result.Value;
                RetentionRulesAcknowledged = false;
            }

            RetentionResultText = FormatRetentionRunResult(result.Value);
            RetentionProgressText = Loc.T("repo_settings.retention_completed");
            RetentionProgressValue = 100;
            IsRetentionProgressIndeterminate = false;

            if (!dryRun)
            {
                if (hasUnsavedRetentionPolicyChanges)
                {
                    RetentionLastRunText = FormatNeverOrDate(result.Value.FinishedAtUtc);
                    RetentionLastStatusText = result.Value.Summary;
                }
                else
                {
                    await LoadAsync(RepositoryId);
                }
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
            RetentionProgressValue = 0;
            IsRetentionProgressIndeterminate = false;
            _retentionCts?.Dispose();
            _retentionCts = null;
        }
    }

    private void ApplyRetentionPolicy(RepositoryRetentionPolicyDto policy)
    {
        _lastAppliedRetentionPolicy = policy;
        RetentionUseLocalPolicy = policy.HasLocalOverride;
        RetentionPolicySource = policy.PolicySource;
        ApplyRetentionEditableState(policy);
        ApplyRetentionStatusState(policy);
    }

    private void ApplyRetentionEditableState(RepositoryRetentionPolicyDto policy)
    {
        _manualRetentionCleanupConfirmedForCurrentPolicy = false;
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
        RetentionMaintenanceWindowEnabled = policy.MaintenanceWindowStartHour is not null
            && policy.MaintenanceWindowEndHour is not null;
        SelectedRetentionMaintenanceWindowStartHour = policy.MaintenanceWindowStartHour ?? 1;
        SelectedRetentionMaintenanceWindowEndHour = policy.MaintenanceWindowEndHour ?? 5;
        RetentionArchiveMode = RepositoryRetentionStorageModes.IsArchive(policy.StorageMode);
        RetentionManualCleanupAllowed = policy.AllowManualSnapshotCleanup;
        RetentionAutomaticCompactionEnabled = policy.AutomaticCompactionEnabled;
        SelectedRetentionAutomaticCompactionWindowHours = policy.AutomaticCompactionWindowHours is > 0
            ? policy.AutomaticCompactionWindowHours.Value
            : 24;
        SyncManualHistorySafeModeFromState();
    }

    private void ApplyRetentionStatusState(RepositoryRetentionPolicyDto policy)
    {
        RetentionLastRunText = FormatNeverOrDate(policy.LastRunAtUtc);
        RetentionLastStatusText = HumanizeRetentionStatus(policy.LastStatus);
    }

    private static string HumanizeRetentionStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Loc.T("common.not_available_short");

        if (raw.StartsWith("No snapshots found", StringComparison.OrdinalIgnoreCase))
            return Loc.T("repo_settings.retention_status_no_changes");

        var isDryRun = raw.StartsWith("Dry-run:", StringComparison.OrdinalIgnoreCase);
        var isApplied = raw.StartsWith("Applied:", StringComparison.OrdinalIgnoreCase);

        if (!isDryRun && !isApplied)
            return raw;

        var snapshots = ExtractRetentionStatusInt(raw, "snapshots");
        var freed = ExtractRetentionStatusFreed(raw);
        var key = isDryRun ? "repo_settings.retention_status_dry_run" : "repo_settings.retention_status_applied";
        return Loc.F(key, snapshots, freed);
    }

    private static int ExtractRetentionStatusInt(string raw, string field)
    {
        var marker = field + "=";
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        idx += marker.Length;
        var end = raw.IndexOfAny([',', ' '], idx);
        var token = end < 0 ? raw[idx..] : raw[idx..end];
        return int.TryParse(token, out var n) ? n : 0;
    }

    private static string ExtractRetentionStatusFreed(string raw)
    {
        var marker = "freed=";
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "0 B";
        idx += marker.Length;
        var end = raw.IndexOfAny([',', ')'], idx);
        return (end < 0 ? raw[idx..] : raw[idx..end]).Trim();
    }

    private static string FormatRetentionRunResult(RepositoryRetentionRunResultDto result)
        => Loc.F(
            result.DryRun
                ? "repo_settings.retention_preview_completed_detail"
                : "repo_settings.retention_cleanup_completed_detail",
            result.SnapshotsMarked,
            result.FileVersionsMarked,
            result.BlockFilesDeleted,
            FormatBytes(result.EstimatedFreedBytes));

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
            NotifyCloudSyncPresentationChanged();
            UpdateCloudSyncStatusAutoRefreshState();
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
        NotifyCloudSyncPresentationChanged();
        UpdateCloudSyncStatusAutoRefreshState();
    }

    private void ApplyCloudStorageFootprintState(RepositoryCloudSyncStatusDto? status)
    {
        CloudRepositorySnapshotTotalSizeBytes = null;
        CloudRepositoryStorageState = !HasCloudAccess
            ? "local_only"
            : status?.LastRemoteSnapshotId is > 0
                ? "loading"
                : "no_remote";
    }

    private async Task RefreshCloudStorageFootprintAsync(int repositoryId, RepositoryCloudSyncStatusDto? status)
    {
        if (repositoryId <= 0 || RepositoryId != repositoryId)
            return;

        if (!HasCloudAccess)
        {
            await RunOnUiAsync(() =>
            {
                CloudRepositoryStorageState = "local_only";
            });
            return;
        }

        if (status?.LastRemoteSnapshotId is not > 0)
        {
            await RunOnUiAsync(() =>
            {
                CloudRepositoryStorageState = "no_remote";
            });
            return;
        }

        try
        {
            var activeProfile = await _userProfiles.GetActiveProfileAsync();
            var evaluation = _tokenPolicy.Evaluate(activeProfile?.AccessToken);
            if (!evaluation.CanUseForSync || string.IsNullOrWhiteSpace(activeProfile?.AccessToken))
            {
                if (RepositoryId == repositoryId)
                {
                    await RunOnUiAsync(() =>
                    {
                        CloudRepositorySnapshotTotalSizeBytes = null;
                        CloudRepositoryStorageState = "auth_required";
                    });
                }
                return;
            }

            var package = await _cloudSyncService.GetLatestSnapshotAsync(activeProfile.AccessToken!, repositoryId);
            if (RepositoryId != repositoryId)
                return;

            if (package?.Snapshot is null)
            {
                await RunOnUiAsync(() =>
                {
                    CloudRepositorySnapshotTotalSizeBytes = null;
                    CloudRepositoryStorageState = "no_remote";
                });
                return;
            }

            var latestSnapshotSizeBytes = Math.Max(0L, CalculateLatestUploadPayloadSize(package));
            await RunOnUiAsync(() =>
            {
                CloudRepositorySnapshotTotalSizeBytes = latestSnapshotSizeBytes;
                CloudRepositoryStorageState = "latest";
            });
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Repository cloud storage footprint refresh failed. RepositoryId {RepositoryId}", repositoryId);
            if (RepositoryId == repositoryId)
            {
                await RunOnUiAsync(() =>
                {
                    CloudRepositorySnapshotTotalSizeBytes = null;
                    CloudRepositoryStorageState = "unavailable";
                });
            }
        }
    }

    private RepositoryRetentionPolicyDto BuildRetentionPolicyFromState()
    {
        var maxAgeDays = ParseNullableInt(RetentionMaxAgeDays);
        var maxSnapshots = ParseNullableInt(RetentionMaxSnapshots);
        var maxTotalSizeMb = ParseNullableDouble(RetentionMaxTotalSizeMb);
        long? maxTotalSizeBytes = null;

        if (maxTotalSizeMb is > 0)
            maxTotalSizeBytes = (long)Math.Round(maxTotalSizeMb.Value * 1024d * 1024d, MidpointRounding.AwayFromZero);

        var triggers = BuildRetentionTriggerFiltersFromState();

        return new RepositoryRetentionPolicyDto(
            RetentionEnabled,
            maxAgeDays,
            maxSnapshots,
            maxTotalSizeBytes,
            triggers,
            Math.Clamp(RetentionRunIntervalMinutes, 5, 7 * 24 * 60),
            RetentionMaintenanceWindowEnabled ? SelectedRetentionMaintenanceWindowStartHour : null,
            RetentionMaintenanceWindowEnabled ? SelectedRetentionMaintenanceWindowEndHour : null,
            LastRunAtUtc: null,
            LastStatus: null,
            StorageMode: RetentionArchiveMode ? RepositoryRetentionStorageModes.Archive : RepositoryRetentionStorageModes.Delete,
            AllowManualSnapshotCleanup: RetentionManualCleanupAllowed,
            AutomaticCompactionEnabled: RetentionAutomaticCompactionEnabled,
            AutomaticCompactionWindowHours: RetentionAutomaticCompactionEnabled
                ? SelectedRetentionAutomaticCompactionWindowHours
                : null,
            HasLocalOverride: RetentionUseLocalPolicy,
            PolicySource: RetentionUseLocalPolicy
                ? RepositoryRetentionPolicySources.Repository
                : RepositoryRetentionPolicySources.None,
            SourceRepositoryId: RetentionUseLocalPolicy ? RepositoryId : null);
    }

    private RepositorySettingsSnapshot BuildSettingsSnapshot()
    {
        var policy = BuildRetentionPolicyFromState();
        var retryAttempts = ParseIntOrDefault(SyncRetryMaxAttempts, 5, 1, 20);
        var retryDelay = ParseIntOrDefault(SyncRetryBaseDelaySeconds, 30, 5, 600);
        var strategy = RepositorySyncConflictStrategies.Normalize(SyncConflictStrategy);
        return BuildSettingsSnapshot(policy, strategy, retryAttempts, retryDelay);
    }

    private RepositorySettingsSnapshot BuildSettingsSnapshot(
        RepositoryRetentionPolicyDto policy,
        string syncConflictStrategy,
        int syncRetryAttempts,
        int syncRetryDelaySeconds)
    {
        var normalizedPolicy = NormalizeRetentionPolicy(policy);
        var retentionKey = string.Join("|", new[]
        {
            normalizedPolicy.HasLocalOverride.ToString(CultureInfo.InvariantCulture),
            normalizedPolicy.Enabled.ToString(CultureInfo.InvariantCulture),
            normalizedPolicy.MaxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            normalizedPolicy.MaxSnapshots?.ToString(CultureInfo.InvariantCulture) ?? "",
            normalizedPolicy.MaxTotalSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
            string.Join(",", normalizedPolicy.TriggerFilters),
            normalizedPolicy.RunIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            normalizedPolicy.MaintenanceWindowStartHour?.ToString(CultureInfo.InvariantCulture) ?? "",
            normalizedPolicy.MaintenanceWindowEndHour?.ToString(CultureInfo.InvariantCulture) ?? "",
            RepositoryRetentionStorageModes.Normalize(normalizedPolicy.StorageMode),
            normalizedPolicy.AllowManualSnapshotCleanup.ToString(CultureInfo.InvariantCulture),
            normalizedPolicy.AutomaticCompactionEnabled.ToString(CultureInfo.InvariantCulture),
            normalizedPolicy.AutomaticCompactionWindowHours?.ToString(CultureInfo.InvariantCulture) ?? "",
            RepositoryRetentionPolicySources.Normalize(normalizedPolicy.PolicySource),
            normalizedPolicy.SourceRepositoryId?.ToString(CultureInfo.InvariantCulture) ?? ""
        });

        var cloudKey = string.Join("|", new[]
        {
            ProtectCloudMetadata.ToString(CultureInfo.InvariantCulture),
            RepositorySyncConflictStrategies.Normalize(syncConflictStrategy),
            syncRetryAttempts.ToString(CultureInfo.InvariantCulture),
            syncRetryDelaySeconds.ToString(CultureInfo.InvariantCulture)
        });

        return new RepositorySettingsSnapshot(
            RepositoryId,
            (RepositoryName ?? string.Empty).Trim(),
            Description?.Trim() ?? string.Empty,
            NormalizeDirectoryPathForSnapshot(DirectoryPath),
            BuildNormalizedFormatsKey(SelectedFormats),
            AutoCaptureFileVersions,
            ProtectCloudMetadata,
            BuildNormalizedPatternsKey(ExcludedPatterns),
            retentionKey,
            RepositorySyncConflictStrategies.Normalize(syncConflictStrategy),
            syncRetryAttempts,
            syncRetryDelaySeconds,
            cloudKey);
    }

    private void RefreshSettingsDirtyState()
    {
        HasUnsavedSettingsChanges = _lastSavedSettingsSnapshot is not null
                                    && !BuildSettingsSnapshot().Equals(_lastSavedSettingsSnapshot);
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
    }

    private bool HasPendingCustomFormat
    {
        get
        {
            var normalized = NormalizeFormat(CustomFormat);
            return !string.IsNullOrWhiteSpace(normalized)
                   && !SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ApplyPendingCustomFormat()
    {
        var normalized = NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized)
            || SelectedFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _allFormatOptions.Add(normalized);
        SelectedFormats.Add(normalized);
        CustomFormat = string.Empty;
    }

    private static string BuildNormalizedFormatsKey(IEnumerable<string> values)
        => string.Join("|", values
            .Select(NormalizeFormat)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    private static string BuildNormalizedPatternsKey(IEnumerable<string> values)
        => string.Join("|", values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().Replace('\\', '/').Trim('/'))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    private static string NormalizeDirectoryPathForSnapshot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFullPath(value.Trim().Replace('/', Path.DirectorySeparatorChar))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim().TrimEnd('\\', '/');
        }
    }

    private sealed record RepositorySettingsSnapshot(
        int RepositoryId,
        string Name,
        string Description,
        string DirectoryPath,
        string FormatsKey,
        bool AutoCaptureFileVersions,
        bool ProtectCloudMetadata,
        string ExcludedPatternsKey,
        string RetentionKey,
        string SyncConflictStrategy,
        int SyncRetryMaxAttempts,
        int SyncRetryBaseDelaySeconds,
        string CloudKey);

    private string? ValidateRetentionPolicyBeforeSave(RepositoryRetentionPolicyDto policy)
    {
        if (!policy.HasLocalOverride)
            return null;

        if (!policy.Enabled || !HasRetentionPolicyChanges(policy))
            return null;

        if (NeedsManualCleanupUnlock(policy))
            return Loc.T("repo_settings.retention_manual_cleanup_required");

        if (policy.AutomaticCompactionEnabled && policy.AutomaticCompactionWindowHours is not > 0)
            return Loc.T("repo_settings.retention_compaction_window_required");

        return null;
    }

    private bool HasRetentionPolicyChanges(RepositoryRetentionPolicyDto currentPolicy)
    {
        if (_lastAppliedRetentionPolicy is null)
            return currentPolicy.Enabled;

        var current = NormalizeRetentionPolicy(currentPolicy);
        var applied = NormalizeRetentionPolicy(_lastAppliedRetentionPolicy);

        if (current.HasLocalOverride != applied.HasLocalOverride
            || current.Enabled != applied.Enabled
            || current.MaxAgeDays != applied.MaxAgeDays
            || current.MaxSnapshots != applied.MaxSnapshots
            || current.MaxTotalSizeBytes != applied.MaxTotalSizeBytes
            || current.RunIntervalMinutes != applied.RunIntervalMinutes
            || current.MaintenanceWindowStartHour != applied.MaintenanceWindowStartHour
            || current.MaintenanceWindowEndHour != applied.MaintenanceWindowEndHour
            || !string.Equals(current.StorageMode, applied.StorageMode, StringComparison.OrdinalIgnoreCase)
            || current.AllowManualSnapshotCleanup != applied.AllowManualSnapshotCleanup
            || current.AutomaticCompactionEnabled != applied.AutomaticCompactionEnabled
            || current.AutomaticCompactionWindowHours != applied.AutomaticCompactionWindowHours)
        {
            return true;
        }

        return !current.TriggerFilters.SequenceEqual(applied.TriggerFilters, StringComparer.OrdinalIgnoreCase);
    }

    private static RepositoryRetentionPolicyDto NormalizeRetentionPolicy(RepositoryRetentionPolicyDto policy)
    {
        var triggers = policy.TriggerFilters
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (policy.Enabled && triggers.Count == 0)
            triggers = [SafeDefaultRetentionTriggerFilter];

        return policy with
        {
            MaxAgeDays = policy.MaxAgeDays is > 0 ? policy.MaxAgeDays : null,
            MaxSnapshots = policy.MaxSnapshots is > 0 ? policy.MaxSnapshots : null,
            MaxTotalSizeBytes = policy.MaxTotalSizeBytes is > 0 ? policy.MaxTotalSizeBytes : null,
            TriggerFilters = triggers,
            RunIntervalMinutes = Math.Clamp(policy.RunIntervalMinutes, 5, 7 * 24 * 60),
            MaintenanceWindowStartHour = policy.MaintenanceWindowStartHour is >= 0 and <= 23 ? policy.MaintenanceWindowStartHour : null,
            MaintenanceWindowEndHour = policy.MaintenanceWindowEndHour is >= 0 and <= 23 ? policy.MaintenanceWindowEndHour : null,
            StorageMode = RepositoryRetentionStorageModes.Normalize(policy.StorageMode),
            HasLocalOverride = policy.HasLocalOverride,
            PolicySource = RepositoryRetentionPolicySources.Normalize(policy.PolicySource),
            SourceRepositoryId = policy.SourceRepositoryId,
            AutomaticCompactionWindowHours = policy.AutomaticCompactionEnabled && policy.AutomaticCompactionWindowHours is > 0
                ? policy.AutomaticCompactionWindowHours
                : null,
            LastRunAtUtc = null,
            LastStatus = null
        };
    }

    private static string FormatCloudSyncStatus(string? status)
    {
        if (UserExperienceManager.Instance.IsBasicMode)
            return FormatCloudSyncStatusBasic(status);

        if (string.IsNullOrWhiteSpace(status))
            return Loc.T("dashboard.sync.idle");

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" => Loc.T("dashboard.sync.queued"),
            "syncing" or "syncing_prepare" => Loc.T("dashboard.sync.syncing_prepare"),
            "syncing_snapshot" => Loc.T("dashboard.sync.syncing_snapshot"),
            "syncing_finalize" => Loc.T("dashboard.sync.syncing_finalize"),
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => FormatSyncingUploadStatus(status),
            "offline_retry" => Loc.T("dashboard.sync.offline_retry"),
            "retrying" => Loc.T("dashboard.sync.retrying"),
            "auth_required" => Loc.T("dashboard.sync.auth_required"),
            "conflict" => Loc.T("dashboard.sync.conflict"),
            "dead_letter" => Loc.T("dashboard.sync.dead_letter"),
            "failed" => Loc.T("dashboard.sync.failed"),
            "linked" => Loc.T("dashboard.sync.linked"),
            "paused" => Loc.T("dashboard.sync.paused"),
            "cancelled" => Loc.T("dashboard.sync.cancelled"),
            "skipped" => Loc.T("dashboard.sync.skipped"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.synced"),
            _ => Loc.F("dashboard.sync.unknown_status", HumanizeStatusToken(status))
        };
    }

    private static string HumanizeStatusToken(string status)
    {
        var text = status.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }

    private static string FormatSyncingUploadStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Loc.T("dashboard.sync.syncing");

        var parts = status.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var progress = parts[^1].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (progress.Length == 2 &&
                int.TryParse(progress[0], out var current) &&
                int.TryParse(progress[1], out var total))
            {
                return Loc.F("dashboard.sync.syncing_upload", current, total);
            }
        }

        return Loc.T("dashboard.sync.syncing");
    }

    private static string FormatCloudSyncStatusBasic(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Loc.T("repo_settings.sync_status_basic_local");

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => Loc.T("repo_settings.sync_status_basic_working"),
            "queued" or "syncing" or "syncing_prepare" or "syncing_snapshot" or "syncing_finalize" or "offline_retry" or "retrying"
                => Loc.T("repo_settings.sync_status_basic_working"),
            "auth_required" or "conflict" or "failed" or "dead_letter" or "paused" or "cancelled" => Loc.T("repo_settings.sync_status_basic_attention"),
            "linked" => Loc.T("repo_settings.sync_status_basic_ready"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("repo_settings.sync_status_basic_ready"),
            _ => Loc.T("repo_settings.sync_status_basic_local")
        };
    }

    private static string FormatCloudSyncProgressPhase(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "queued" => Loc.T("repo_settings.sync_phase_queued"),
            "syncing_snapshot" => Loc.T("repo_settings.sync_phase_snapshot"),
            "syncing_finalize" => Loc.T("repo_settings.sync_phase_finalize"),
            "offline_retry" => Loc.T("repo_settings.sync_phase_retry"),
            "retrying" => Loc.T("repo_settings.sync_phase_retry"),
            _ when normalized is not null && normalized.StartsWith("syncing_upload", StringComparison.Ordinal)
                => Loc.T("repo_settings.sync_phase_upload"),
            _ => Loc.T("repo_settings.sync_phase_prepare")
        };
    }

    private static string FormatCloudSyncProgressSummary(string? status, int current, int total)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        if (normalized is not null && normalized.StartsWith("syncing_upload", StringComparison.Ordinal))
        {
            return total > 0
                ? Loc.F("repo_settings.sync_progress_summary", FormatUploadProgress(current, total))
                : Loc.T("repo_settings.sync_progress_uploading");
        }

        return normalized switch
        {
            "queued" => Loc.T("repo_settings.sync_progress_queued"),
            "syncing_snapshot" => Loc.T("repo_settings.sync_progress_snapshot"),
            "syncing_finalize" => Loc.T("repo_settings.sync_progress_finalize"),
            "offline_retry" => Loc.T("repo_settings.sync_progress_offline_retry"),
            "retrying" => Loc.T("repo_settings.sync_progress_retrying"),
            _ => Loc.T("repo_settings.sync_progress_preparing")
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowCloudConnectivityHint));
            OnPropertyChanged(nameof(CloudConnectivityHintText));
        });
    }

    private void OnCloudSyncRuntimeStateChanged(CloudSyncRuntimeSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowCloudConnectivityHint));
            OnPropertyChanged(nameof(CloudConnectivityHintText));
            NotifyCloudSyncPresentationChanged();
            UpdateCloudSyncStatusAutoRefreshState();
        });
    }

    private void OnExperienceModeChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        foreach (var category in FormatCategories)
            category.RefreshLocalization();

        var selectedStrategy = SyncConflictStrategy;
        SyncConflictStrategies.Clear();
        foreach (var strategy in RepositorySyncConflictStrategies.All)
            SyncConflictStrategies.Add(strategy);
        SyncConflictStrategy = RepositorySyncConflictStrategies.All.Contains(selectedStrategy, StringComparer.OrdinalIgnoreCase)
            ? selectedStrategy
            : RepositorySyncConflictStrategies.LastWriteWins;

        OnPropertyChanged(nameof(IsBasicMode));
        OnPropertyChanged(nameof(IsProfessionalMode));
        OnPropertyChanged(nameof(ShowAdvancedSyncSettings));
        OnPropertyChanged(nameof(ShowAdvancedRetentionSettings));
        OnPropertyChanged(nameof(CloudSyncSectionHint));
        OnPropertyChanged(nameof(CloudSyncStatusLabel));
        OnPropertyChanged(nameof(CloudSyncQueueLabel));
        OnPropertyChanged(nameof(SyncLocalStorageText));
        OnPropertyChanged(nameof(SyncCloudStorageText));
        OnPropertyChanged(nameof(SyncCloudStorageHintText));
        OnPropertyChanged(nameof(SyncNowLabel));
        OnPropertyChanged(nameof(CloudRepairTitle));
        OnPropertyChanged(nameof(CloudRepairButtonLabel));
        OnPropertyChanged(nameof(CloudRepairHint));
        OnPropertyChanged(nameof(CloudGuestHint));
        OnPropertyChanged(nameof(ShowCloudConnectivityHint));
        OnPropertyChanged(nameof(CloudConnectivityHintText));
        NotifyCloudSyncPresentationChanged();
        OnPropertyChanged(nameof(RetentionPolicyTitle));
        OnPropertyChanged(nameof(RetentionSectionHint));
        OnPropertyChanged(nameof(CanRunSyncNow));
        OnPropertyChanged(nameof(CanRunCloudRepair));
        OnPropertyChanged(nameof(CanRunRetention));
        OnPropertyChanged(nameof(CanRunRetentionDryRun));
        OnPropertyChanged(nameof(CanRunRetentionApply));
        OnPropertyChanged(nameof(HasRetentionPreview));
        OnPropertyChanged(nameof(RequiresManualCleanupUnlock));
        OnPropertyChanged(nameof(IsRetentionAutomaticCompactionConfigured));
        OnPropertyChanged(nameof(RetentionAutomaticCompactionSummaryText));
        OnPropertyChanged(nameof(IsRetentionMaintenanceWindowConfigured));
        OnPropertyChanged(nameof(RetentionMaintenanceWindowSummaryText));
        OnPropertyChanged(nameof(RetentionStorageModeSummaryText));
        OnPropertyChanged(nameof(RetentionPreviewStateText));
        OnPropertyChanged(nameof(RetentionPreviewSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewManualSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewManualBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewWorkingSnapshotsText));
        OnPropertyChanged(nameof(RetentionPreviewWorkingBreakdownText));
        OnPropertyChanged(nameof(RetentionPreviewAutomaticCompactionText));
        OnPropertyChanged(nameof(RetentionPreviewVersionsText));
        OnPropertyChanged(nameof(RetentionPreviewDiffsText));
        OnPropertyChanged(nameof(RetentionPreviewBlocksText));
        OnPropertyChanged(nameof(RetentionPreviewFreedSpaceText));
        OnPropertyChanged(nameof(RetentionPreviewSummaryText));
        OnPropertyChanged(nameof(RetentionPreviewImpactText));
        OnPropertyChanged(nameof(RetentionPrerequisitesText));
        OnPropertyChanged(nameof(ShowGuestCloudHint));
        OnPropertyChanged(nameof(ShowBlockingOverlay));
        OnPropertyChanged(nameof(BlockingOverlayTitle));
        OnPropertyChanged(nameof(BlockingOverlayDetail));

        if (_lastAppliedRetentionPolicy is not null)
            ApplyRetentionStatusState(_lastAppliedRetentionPolicy);
        else
        {
            RetentionLastRunText = Loc.T("common.never");
            RetentionLastStatusText = Loc.T("common.not_available_short");
        }

        ApplyCloudSyncStatus(_lastAppliedCloudSyncStatus);
    }

    private void NotifyCloudSyncPresentationChanged()
    {
        OnPropertyChanged(nameof(ShowCloudSyncProgressCard));
        OnPropertyChanged(nameof(HasMeasuredCloudSyncProgress));
        OnPropertyChanged(nameof(ShowCloudSyncProgressPercent));
        OnPropertyChanged(nameof(CloudSyncProgressIsIndeterminate));
        OnPropertyChanged(nameof(CloudSyncProgressValue));
        OnPropertyChanged(nameof(CloudSyncProgressPhaseText));
        OnPropertyChanged(nameof(CloudSyncProgressPercentText));
        OnPropertyChanged(nameof(CloudSyncProgressSummaryText));
        OnPropertyChanged(nameof(CloudSyncProgressEtaText));
        OnPropertyChanged(nameof(CloudSyncProgressElapsedText));
        OnPropertyChanged(nameof(CloudSyncProgressLastUpdateText));
    }

    private void UpdateCloudSyncStatusAutoRefreshState()
    {
        var shouldRun = RepositoryId > 0
            && HasCloudAccess
            && (IsSyncNowRunning || HasActiveCloudSyncWork(_lastAppliedCloudSyncStatus));

        if (shouldRun)
        {
            if (_cloudSyncStatusRefreshCts is not null)
                return;

            _cloudSyncStatusRefreshCts = new CancellationTokenSource();
            var token = _cloudSyncStatusRefreshCts.Token;
            _cloudSyncStatusRefreshTask = Task.Run(() => RunCloudSyncStatusAutoRefreshAsync(token), token);
            return;
        }

        if (_cloudSyncStatusRefreshCts is null)
            return;

        _cloudSyncStatusRefreshCts.Cancel();
        _cloudSyncStatusRefreshCts.Dispose();
        _cloudSyncStatusRefreshCts = null;
        _cloudSyncStatusRefreshTask = null;
    }

    private async Task RunCloudSyncStatusAutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshCloudSyncStatusSnapshotAsync(ct);
                await Task.Delay(CloudSyncStatusRefreshInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Repository settings cloud sync auto-refresh loop failed");
        }
    }

    private async Task RefreshCloudSyncStatusSnapshotAsync(CancellationToken ct = default)
    {
        if (RepositoryId <= 0)
            return;

        try
        {
            var repositoryId = RepositoryId;
            if (IsLoading)
                return;

            var repo = await SendMediatorAsync(new GetRepositoryDetailQuery(repositoryId), ct);
            if (repo is null || RepositoryId != repositoryId)
                return;

            await RunOnUiAsync(() =>
            {
                ApplyCloudSyncStatus(repo.CloudSync);
            });

            if (!HasActiveCloudSyncWork(repo.CloudSync))
                _ = RefreshCloudStorageFootprintAsync(repositoryId, repo.CloudSync);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Repository settings cloud sync snapshot refresh failed. RepositoryId {RepositoryId}", RepositoryId);
        }
    }

    private string? ResolveCloudConnectivityMessage()
        => _cloudSyncRuntime.IsPaused
            ? Loc.T("ui_error.cloud_actions_paused")
            : _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("ui_error.internet_required"),
            ConnectivityState.CloudUnavailable => Loc.T("ui_error.cloud_temporarily_unavailable"),
            _ => null
        };

    private Task<TResponse> SendMediatorAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        => _scopeExecutor.ExecuteAsync<IMediator, TResponse>((mediator, token) => mediator.Send(request, token), ct);

    private Task SendMediatorAsync(IRequest request, CancellationToken ct = default)
        => _scopeExecutor.ExecuteAsync<IMediator>((mediator, token) => mediator.Send(request, token), ct);

    private static string FormatNeverOrDate(DateTime? value)
        => value is null
            ? Loc.T("common.never")
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatCloudQueueSummary(int pending, int running, int retry, int conflict, int deadLetter)
    {
        if (pending == 0 && running == 0 && retry == 0 && conflict == 0 && deadLetter == 0)
            return Loc.T("repo_settings.queue_basic_idle");

        if (conflict > 0 || deadLetter > 0)
            return Loc.T("repo_settings.sync_status_basic_attention");

        return Loc.T("repo_settings.queue_basic_active");
    }

    private static bool HasActiveCloudSyncWork(RepositoryCloudSyncStatusDto? cloud)
    {
        if (cloud is null)
            return false;

        if (cloud.PendingQueueCount > 0 || cloud.RunningQueueCount > 0 || cloud.RetryQueueCount > 0)
            return true;

        if (cloud.UploadProgressTotal > 0 && cloud.UploadProgressCurrent < cloud.UploadProgressTotal)
            return true;

        var normalizedStatus = cloud.LastStatus?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedStatus))
            return false;

        return normalizedStatus.StartsWith("syncing_upload", StringComparison.Ordinal)
               || normalizedStatus is "queued"
                   or "syncing"
                   or "syncing_prepare"
                   or "syncing_snapshot"
                   or "syncing_finalize"
                   or "offline_retry"
                   or "retrying";
    }

    private static string FormatUploadProgress(int current, int total)
    {
        if (total <= 0)
            return Loc.T("common.not_available_short");

        return Loc.F("app_settings.repository_sync_health_progress_format", Math.Clamp(current, 0, total), total);
    }

    private static double CalculateProgressPercent(int current, int total)
    {
        if (total <= 0)
            return 0;

        return Math.Clamp((double)Math.Clamp(current, 0, total) / total * 100d, 0d, 100d);
    }

    private static string FormatEtaText(int current, int total, DateTime? startedAtUtc, DateTime? updatedAtUtc)
    {
        if (total <= 0)
            return Loc.T("common.not_available_short");

        current = Math.Clamp(current, 0, total);
        if (current >= total)
            return Loc.T("app_settings.repository_sync_health_eta_done");

        if (startedAtUtc is null)
            return Loc.T("app_settings.repository_sync_health_eta_calculating");

        var referenceUtc = updatedAtUtc ?? DateTime.UtcNow;
        var elapsed = referenceUtc - startedAtUtc.Value;
        if (elapsed <= TimeSpan.FromSeconds(2) || current <= 0)
            return Loc.T("app_settings.repository_sync_health_eta_calculating");

        var rate = current / elapsed.TotalSeconds;
        if (rate <= 0.0001d)
            return Loc.T("app_settings.repository_sync_health_eta_calculating");

        var remainingSeconds = (total - current) / rate;
        if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
            return Loc.T("app_settings.repository_sync_health_eta_calculating");

        return Loc.F("app_settings.repository_sync_health_eta_format", FormatDuration(TimeSpan.FromSeconds(remainingSeconds)));
    }

    private static string FormatElapsedText(int current, int total, DateTime? startedAtUtc, DateTime? updatedAtUtc)
    {
        if (total <= 0 || startedAtUtc is null)
            return Loc.T("common.not_available_short");

        var referenceUtc = updatedAtUtc ?? DateTime.UtcNow;
        var elapsed = referenceUtc - startedAtUtc.Value;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return Loc.F("app_settings.repository_sync_health_elapsed_format", FormatDuration(elapsed));
    }

    private static string FormatLastProgressUpdateText(DateTime? updatedAtUtc)
    {
        if (updatedAtUtc is null)
            return Loc.T("common.not_available_short");

        return Loc.F(
            "app_settings.repository_sync_health_last_update_format",
            updatedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss"));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0s";

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{Math.Max(1, duration.Seconds)}s";
    }

    private static long CalculateLatestUploadPayloadSize(CloudSnapshotPackageDto package)
    {
        if (package.FileVersions.Count == 0)
            return Math.Max(0L, package.Snapshot.TotalFileBytes);

        var uniqueBlocks = package.FileVersions
            .SelectMany(version => version.Blocks)
            .Where(block => !string.IsNullOrWhiteSpace(block.BlockHash))
            .GroupBy(block => block.BlockHash, StringComparer.OrdinalIgnoreCase)
            .Select(group => (long)Math.Max(0, group.First().StoredSizeBytes))
            .ToList();

        if (uniqueBlocks.Count == 0)
            return Math.Max(0L, package.Snapshot.TotalFileBytes);

        return Math.Max(0L, uniqueBlocks.Sum());
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var format = value >= 100 || unitIndex == 0 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }

    private async Task<bool> ConfirmRepositoryDeletionAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.ConfigureLocalized(
                "repo_settings.delete_confirm_title",
                "repo_settings.delete_confirm_body",
                [RepositoryName],
                "repo_settings.delete_confirm_warning",
                "repo_settings.delete_confirm_button");
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

    private string FormatRetentionKindBreakdown(int count)
    {
        var total = RetentionPreview?.SnapshotsMarked ?? 0;
        if (total <= 0 || count <= 0)
            return Loc.T("repo_settings.retention_preview_share_zero");

        return Loc.F("repo_settings.retention_preview_share_format", FormatSnapshotShare(count, total));
    }

    private string BuildRetentionPreviewImpactText(RepositoryRetentionRunResultDto? preview)
    {
        if (preview is null)
            return Loc.T("repo_settings.retention_preview_impact_needed");

        if (!preview.PolicyApplied || preview.SnapshotsMarked <= 0)
            return Loc.T("repo_settings.retention_preview_impact_none");

        if (preview.ArchiveMode)
        {
            if (preview.ManualSnapshotsMarked > 0)
            {
                return Loc.F(
                    "repo_settings.retention_preview_impact_archive_with_manual",
                    preview.SnapshotsArchived > 0 ? preview.SnapshotsArchived : preview.SnapshotsMarked,
                    preview.ManualSnapshotsMarked);
            }

            return Loc.F(
                "repo_settings.retention_preview_impact_archive_only",
                preview.SnapshotsArchived > 0 ? preview.SnapshotsArchived : preview.SnapshotsMarked);
        }

        var automatic = preview.AutomaticSnapshotsMarked;
        var manual = preview.ManualSnapshotsMarked;
        var working = preview.WorkingSnapshotsMarked;

        if (manual > 0 && automatic == 0 && working == 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_manual_only", manual),
                preview.AutomaticSnapshotsCompacted);

        if (manual > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_includes_manual", manual),
                preview.AutomaticSnapshotsCompacted);

        if (automatic > 0 && working > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_auto_and_working_only", automatic, working),
                preview.AutomaticSnapshotsCompacted);

        if (automatic > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_automatic_only", automatic),
                preview.AutomaticSnapshotsCompacted);

        if (working > 0)
            return AppendAutomaticCompactionImpact(
                Loc.F("repo_settings.retention_preview_impact_working_only", working),
                preview.AutomaticSnapshotsCompacted);

        return Loc.T("repo_settings.retention_preview_impact_none");
    }

    private bool NeedsManualCleanupUnlock(RepositoryRetentionPolicyDto policy)
    {
        return policy.Enabled
               && TargetsManualSnapshots(policy.TriggerFilters)
               && !policy.AllowManualSnapshotCleanup;
    }

    private bool NeedsManualCleanupConfirmation(RepositoryRetentionPolicyDto policy)
    {
        return policy.Enabled
               && policy.AllowManualSnapshotCleanup
               && TargetsManualSnapshots(policy.TriggerFilters)
               && !_manualRetentionCleanupConfirmedForCurrentPolicy;
    }

    private async Task<bool> EnsureManualCleanupConfirmationAsync(RepositoryRetentionPolicyDto policy, bool applyingNow)
    {
        if (!NeedsManualCleanupConfirmation(policy))
            return true;

        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return false;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            vm.ConfigureLocalized(
                "repo_settings.retention_manual_second_confirm_title",
                applyingNow
                    ? "repo_settings.retention_manual_second_confirm_apply_body"
                    : "repo_settings.retention_manual_second_confirm_save_body",
                [RepositoryName],
                "repo_settings.retention_manual_second_confirm_warning",
                applyingNow
                    ? "repo_settings.retention_manual_second_confirm_apply_button"
                    : "repo_settings.retention_manual_second_confirm_save_button");
        }

        await _windows.ShowDialogAsync(window, owner);
        var confirmed = window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
        _manualRetentionCleanupConfirmedForCurrentPolicy = confirmed;
        return confirmed;
    }

    private bool TargetsManualSnapshotsFromState()
        => TargetsManualSnapshots(BuildRetentionTriggerFiltersFromState());

    private void SyncManualHistorySafeModeFromState()
    {
        var shouldEnableSafeMode = !TargetsManualSnapshotsFromState() && !RetentionManualCleanupAllowed;
        if (RetentionManualHistorySafeMode == shouldEnableSafeMode)
            return;

        _syncingManualHistorySafeMode = true;
        RetentionManualHistorySafeMode = shouldEnableSafeMode;
        _syncingManualHistorySafeMode = false;
    }

    private void SyncRetentionTriggerSelectionFromFilter(string value)
    {
        if (_syncingRetentionTriggerSelection)
            return;

        var triggers = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static trigger => !string.IsNullOrWhiteSpace(trigger))
            .Select(static trigger => trigger.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includeAll = triggers.Contains("all");

        _syncingRetentionTriggerSelection = true;
        try
        {
            RetentionIncludesAutomaticSnapshots = includeAll || triggers.Contains("automatic") || triggers.Contains("auto");
            RetentionIncludesManualSnapshots = includeAll || triggers.Contains("manual");
            RetentionIncludesWorkingSnapshots = includeAll || triggers.Contains("working");
            SelectedRetentionTriggerPresetIndex = GetRetentionTriggerPresetIndex(
                RetentionIncludesAutomaticSnapshots,
                RetentionIncludesManualSnapshots,
                RetentionIncludesWorkingSnapshots);
        }
        finally
        {
            _syncingRetentionTriggerSelection = false;
        }
    }

    private void ApplyRetentionTriggerPresetFromIndex(int value)
    {
        if (_syncingRetentionTriggerSelection)
            return;

        _syncingRetentionTriggerSelection = true;
        try
        {
            (RetentionIncludesAutomaticSnapshots, RetentionIncludesManualSnapshots, RetentionIncludesWorkingSnapshots) = value switch
            {
                1 => (true, false, true),
                2 => (true, true, false),
                3 => (false, true, false),
                4 => (false, false, true),
                5 => (false, true, true),
                6 => (true, true, true),
                _ => (true, false, false)
            };
        }
        finally
        {
            _syncingRetentionTriggerSelection = false;
        }

        SyncRetentionTriggerFilterFromSelection();
    }

    private static int GetRetentionTriggerPresetIndex(bool includeAutomatic, bool includeManual, bool includeWorking)
    {
        return (includeAutomatic, includeManual, includeWorking) switch
        {
            (true, false, true) => 1,
            (true, true, false) => 2,
            (false, true, false) => 3,
            (false, false, true) => 4,
            (false, true, true) => 5,
            (true, true, true) => 6,
            _ => 0
        };
    }

    private void SyncRetentionTriggerFilterFromSelection()
    {
        if (_syncingRetentionTriggerSelection)
            return;

        var triggers = new List<string>(3);
        if (RetentionIncludesAutomaticSnapshots)
            triggers.Add("automatic");
        if (RetentionIncludesManualSnapshots)
            triggers.Add("manual");
        if (RetentionIncludesWorkingSnapshots)
            triggers.Add("working");

        if (RetentionEnabled && triggers.Count == 0)
        {
            _syncingRetentionTriggerSelection = true;
            try
            {
                RetentionIncludesAutomaticSnapshots = true;
                triggers.Add("automatic");
            }
            finally
            {
                _syncingRetentionTriggerSelection = false;
            }
        }

        var nextValue = string.Join(", ", triggers);
        if (string.Equals(RetentionTriggerFilter, nextValue, StringComparison.OrdinalIgnoreCase))
        {
            OnRetentionPolicyEdited();
            return;
        }

        RetentionTriggerFilter = nextValue;
    }

    private void ApplyManualHistorySafeMode()
    {
        var currentTriggers = BuildRetentionTriggerFiltersFromState();
        var safeTriggers = currentTriggers
            .SelectMany(static value => string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
                ? new[] { "automatic", "working" }
                : new[] { value })
            .Where(static value =>
                !string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (RetentionEnabled && safeTriggers.Count == 0)
            safeTriggers = [SafeDefaultRetentionTriggerFilter];

        var safeTriggerText = safeTriggers.Count == 0 ? string.Empty : string.Join(", ", safeTriggers);
        var currentTriggerText = currentTriggers.Count == 0 ? string.Empty : string.Join(", ", currentTriggers);

        if (RetentionManualCleanupAllowed)
            RetentionManualCleanupAllowed = false;

        if (!string.Equals(currentTriggerText, safeTriggerText, StringComparison.OrdinalIgnoreCase))
            RetentionTriggerFilter = safeTriggerText;
        else
            OnRetentionPolicyEdited();
    }

    private string BuildRetentionHowItWorksText()
    {
        if (!RetentionEnabled)
            return Loc.T("repo_settings.retention_simple_disabled");

        var parts = new List<string>
        {
            BuildRetentionScopeText(),
            BuildRetentionRuleText(),
            BuildRetentionStorageText(),
            BuildRetentionManualHistoryText(),
            BuildRetentionProtectedTagsText(),
            BuildRetentionScheduleText(),
            BuildRetentionCompactionText()
        };

        return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private string BuildRetentionScopeText()
    {
        var triggers = BuildRetentionTriggerFiltersFromState();
        var touchesAutomatic = triggers.Any(static value =>
            string.Equals(value, "automatic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        var touchesManual = triggers.Any(static value =>
            string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        var touchesWorking = triggers.Any(static value =>
            string.Equals(value, "working", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));

        return (touchesAutomatic, touchesManual, touchesWorking) switch
        {
            (true, false, false) => Loc.T("repo_settings.retention_simple_scope_automatic_only"),
            (false, true, false) => Loc.T("repo_settings.retention_simple_scope_manual_only"),
            (false, false, true) => Loc.T("repo_settings.retention_simple_scope_working_only"),
            (true, true, false) => Loc.T("repo_settings.retention_simple_scope_automatic_manual"),
            (true, false, true) => Loc.T("repo_settings.retention_simple_scope_automatic_working"),
            (false, true, true) => Loc.T("repo_settings.retention_simple_scope_manual_working"),
            (true, true, true) => Loc.T("repo_settings.retention_simple_scope_all"),
            _ => Loc.T("repo_settings.retention_simple_scope_automatic_only")
        };
    }

    private string BuildRetentionRuleText()
    {
        var ruleParts = new List<string>();

        if (ParseNullableInt(RetentionMaxAgeDays) is { } maxAgeDays)
            ruleParts.Add(Loc.F("repo_settings.retention_simple_rule_age", maxAgeDays));
        if (ParseNullableInt(RetentionMaxSnapshots) is { } maxSnapshots)
            ruleParts.Add(Loc.F("repo_settings.retention_simple_rule_count", maxSnapshots));
        if (ParseNullableDouble(RetentionMaxTotalSizeMb) is { } maxSizeMb)
            ruleParts.Add(Loc.F("repo_settings.retention_simple_rule_size", maxSizeMb.ToString("0.##", CultureInfo.InvariantCulture)));

        if (ruleParts.Count == 0)
            return Loc.T("repo_settings.retention_simple_rule_none");

        return Loc.F("repo_settings.retention_simple_rule_prefix", string.Join(", ", ruleParts));
    }

    private string BuildRetentionManualHistoryText()
    {
        if (!TargetsManualSnapshotsFromState())
            return Loc.T("repo_settings.retention_simple_manual_protected");

        return RetentionManualCleanupAllowed
            ? Loc.T("repo_settings.retention_simple_manual_enabled")
            : Loc.T("repo_settings.retention_simple_manual_locked");
    }

    private static string BuildRetentionProtectedTagsText()
        => Loc.T("repo_settings.retention_simple_protected_tags");

    private string BuildRetentionStorageText()
    {
        return RetentionArchiveMode
            ? Loc.T("repo_settings.retention_simple_storage_archive")
            : Loc.T("repo_settings.retention_simple_storage_delete");
    }

    private string BuildRetentionScheduleText()
    {
        if (!RetentionMaintenanceWindowEnabled
            || SelectedRetentionMaintenanceWindowStartHour == SelectedRetentionMaintenanceWindowEndHour)
        {
            return Loc.T("repo_settings.retention_simple_schedule_manual_only");
        }

        return Loc.F(
            "repo_settings.retention_simple_schedule_window",
            FormatHourLabel(SelectedRetentionMaintenanceWindowStartHour),
            FormatHourLabel(SelectedRetentionMaintenanceWindowEndHour));
    }

    private string BuildRetentionCompactionText()
    {
        if (!RetentionAutomaticCompactionEnabled)
            return Loc.T("repo_settings.retention_simple_compaction_off");

        return Loc.F("repo_settings.retention_simple_compaction_on", SelectedRetentionAutomaticCompactionWindowHours);
    }

    private IReadOnlyList<string> BuildRetentionTriggerFiltersFromState()
    {
        var triggers = new List<string>(3);
        if (RetentionIncludesAutomaticSnapshots)
            triggers.Add("automatic");
        if (RetentionIncludesManualSnapshots)
            triggers.Add("manual");
        if (RetentionIncludesWorkingSnapshots)
            triggers.Add("working");

        if (RetentionEnabled && triggers.Count == 0)
            triggers = [SafeDefaultRetentionTriggerFilter];

        return triggers;
    }

    private static bool TargetsManualSnapshots(IReadOnlyCollection<string> triggers)
    {
        return triggers.Any(static value =>
            string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
    }

    private string AppendAutomaticCompactionImpact(string baseImpact, int compactedCount)
    {
        if (compactedCount <= 0)
            return baseImpact;

        return $"{baseImpact} {Loc.F("repo_settings.retention_preview_impact_compaction_suffix", compactedCount)}";
    }

    private static string FormatSnapshotShare(int count, int total)
    {
        if (total <= 0 || count <= 0)
            return "0%";

        var percentage = count * 100d / total;
        return percentage >= 10d || Math.Abs(percentage % 1d) < 0.05d
            ? percentage.ToString("0", CultureInfo.InvariantCulture) + "%"
            : percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatHourLabel(int hour)
        => $"{Math.Clamp(hour, 0, 23):00}:00";

    private static string NormalizeFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return KnownFileExtensions.NormalizeExtension(value) ?? string.Empty;
    }

    private static string NormalizeExclusionPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Trim().Replace('\\', '/').Trim('/');
    }

    private static string NormalizeDirectoryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFullPath(value.Trim().Replace('/', '\\')).TrimEnd('\\', '/');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? TryGetRepositoryRelativePath(string repositoryRoot, string selectedPath)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(repositoryRoot).TrimEnd('\\', '/');
            var normalizedSelected = Path.GetFullPath(selectedPath).TrimEnd('\\', '/');

            if (!normalizedSelected.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            var relative = Path.GetRelativePath(normalizedRoot, normalizedSelected);
            return string.Equals(relative, ".", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : relative.Replace('\\', '/').Trim('/');
        }
        catch
        {
            return null;
        }
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

    private static async Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        await completion.Task;
    }
}
