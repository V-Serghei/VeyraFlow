using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Abstractions.Storage;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Commands.Security;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.AppDiagnostics;
using Veyra.Application.Queries.Repository;
using Veyra.Application.Queries.Security;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Connectivity;
using Veyra.Desktop.Services.Connectivity.Models;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Maintenance;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Monitoring.Models;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Observability;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Services.System;
using Veyra.Desktop.Services.Sync.Runtime;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;
using Veyra.Domain.Observability;

namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed partial class AppSettingsViewModel : ObservableObject
{
    private const string SafeDefaultRetentionTriggerFilter = "automatic";
    private static readonly TimeSpan SyncStatusAutoRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncProgressStallThreshold = TimeSpan.FromSeconds(90);

    private readonly IUserProfileRepository _userProfiles;
    private readonly ILocalCredentialStore _localCredentialStore;
    private readonly IAccessTokenPolicyService _tokenPolicy;
    private readonly IAuthService _auth;
    private readonly IOperationJournalService _journal;
    private readonly IAppDiagnosticsService _appDiagnostics;
    private readonly IRepositoryRetentionService _retention;
    private readonly IRepositoryCloudSyncOrchestrator _sync;
    private readonly ICloudSyncService _cloudSyncService;
    private readonly ILocalBlockStorageMetricsService _localStorageMetrics;
    private readonly IRetentionDefaultsStore _retentionDefaultsStore;
    private readonly IAppTransientStateMaintenanceService _transientStateMaintenance;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly IWindowService _windows;
    private readonly IWindowsAutostartService _autostart;
    private readonly IConnectivityStatusService _connectivity;
    private readonly ICloudSyncRuntimeControlService _cloudSyncRuntime;
    private readonly IMonitoringControlService _monitoringControl;
    private readonly IProcessResourceStatusStore _processResourceStatusStore;
    private readonly ISnapshotScheduler _snapshotScheduler;
    private readonly ISnapshotSchedulerSettingsStore _snapshotSchedulerSettingsStore;
    private readonly IRuntimeObservabilityControlService _runtimeObservability;
    private readonly SnapshotSchedulerOptions _snapshotSchedulerOptions;
    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly IMediator _mediator;
    private readonly ILogger<AppSettingsViewModel> _log;
    private readonly OnboardingStateService _onboardingState;
    private readonly LocalizationManager _localization;
    private readonly ThemeManager _theme;
    private readonly UserExperienceManager _experience;
    private readonly SemaphoreSlim _syncStatusRefreshGate = new(1, 1);

    private bool _suppressLanguageSelectionChanged;
    private bool _suppressThemeSelectionChanged;
    private bool _suppressExperienceSelectionChanged;
    private bool _suppressSensitiveActionToggleChanged;
    private bool _isLanguageRefreshInProgress;
    [ObservableProperty] private bool _isExperienceModeChangeInProgress;
    private bool _hasActiveSyncWork;
    private readonly Dictionary<int, bool> _stallStateByRepositoryId = [];
    private CancellationTokenSource? _syncStatusAutoRefreshCts;
    private Task? _syncStatusAutoRefreshTask;
    private LocalBlockStorageMetricsDto? _lastLocalStorageMetrics;
    private CloudStorageMetricsDto? _lastCloudStorageMetrics;
    private AppDiagnosticsReportDto? _lastDiagnosticsReport;
    private ProcessResourceSnapshotDto? _monitoringProcessResourceSnapshot;
    private UserProfileSessionDto? _lastActiveProfile;
    private CancellationTokenSource? _loadCts;
    private long _loadRequestId;
    private bool _suppressMonitoringEnabledChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsUserTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsAutomationTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsMonitoringTabSelected))]
    [NotifyPropertyChangedFor(nameof(ShowConnectivityBanner))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerAccentColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerBackgroundColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerText))]
    private AppSettingsTabViewModel? _selectedTab;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLoadedPrimaryState;
    [ObservableProperty] private string _activeUsername = Loc.T("app_settings.not_signed_in");
    [ObservableProperty] private string _activeEmail = Loc.T("common.not_available_short");
    [ObservableProperty] private string _activeCloudUserText = Loc.T("common.not_available_short");
    [ObservableProperty] private string _activeSessionTokenState = Loc.T("app_settings.no_token");
    [ObservableProperty] private string _tokenPolicyHint = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private string _usernameInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private string _emailInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private string _passwordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private string _confirmPasswordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(SubmitAuthLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleAuthLabel))]
    [NotifyPropertyChangedFor(nameof(IsConfirmPasswordVisible))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private bool _isRegisterMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmitAuth))]
    private bool _isAuthBusy;

    [ObservableProperty] private string _authMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncToCloud))]
    private bool _isSyncBusy;
    [ObservableProperty] private bool _isCloudMaintenanceBusy;
    [ObservableProperty] private string _syncMessage = string.Empty;
    [ObservableProperty] private string _cloudStorageMessage = string.Empty;
    [ObservableProperty] private string _generalMessage = string.Empty;
    [ObservableProperty] private string _cloudApiBaseUrl = string.Empty;
    [ObservableProperty] private int _localRepositoryCount;
    [ObservableProperty] private bool _hasCloudStorageMetrics;
    [ObservableProperty] private string _cloudStorageLastUpdatedText = string.Empty;
    [ObservableProperty] private long _cloudStorageLogicalBlockCount;
    [ObservableProperty] private long _cloudStoragePhysicalObjectCount;
    [ObservableProperty] private long _cloudStorageMissingBlockCount;
    [ObservableProperty] private string _cloudStorageReductionText = string.Empty;
    [ObservableProperty] private string _cloudStorageLogicalBytesText = string.Empty;
    [ObservableProperty] private string _cloudStoragePhysicalPayloadBytesText = string.Empty;
    [ObservableProperty] private string _cloudStorageBlocksBreakdownText = string.Empty;
    [ObservableProperty] private string _cloudStoragePacksBreakdownText = string.Empty;
    [ObservableProperty] private string _cloudStorageFilesystemBreakdownText = string.Empty;
    [ObservableProperty] private bool _hasLocalStorageMetrics;
    [ObservableProperty] private string _localStorageLastUpdatedText = string.Empty;
    [ObservableProperty] private long _localStorageReferencedBlockCount;
    [ObservableProperty] private long _localStorageUniqueBlockCount;
    [ObservableProperty] private long _localStorageMissingBlockCount;
    [ObservableProperty] private string _localStorageReductionText = string.Empty;
    [ObservableProperty] private string _localStorageLogicalBytesText = string.Empty;
    [ObservableProperty] private string _localStoragePhysicalBytesText = string.Empty;
    [ObservableProperty] private string _localStorageFilesystemBreakdownText = string.Empty;
    [ObservableProperty] private string _localStorageRepositoryBreakdownText = string.Empty;
    [ObservableProperty] private string _localStorageMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSystemDiagnosticsCloudUsageMetric))]
    private bool _hasSystemDiagnostics;
    [ObservableProperty] private bool _isSystemDiagnosticsBusy;
    [ObservableProperty] private string _systemDiagnosticsLastUpdatedText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsTargetRepositoryText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsMemoryText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsMemoryStatusText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsCloudUsageText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsCloudUsageStatusText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsHistoryText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsHistoryStatusText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsScanText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsScanStatusText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsDedupText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsDedupStatusText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsNativeRuntimeText = string.Empty;
    [ObservableProperty] private string _systemDiagnosticsMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeDiagnosticsStateText))]
    [NotifyPropertyChangedFor(nameof(ToggleRuntimeDiagnosticsLabel))]
    [NotifyPropertyChangedFor(nameof(ShowSystemDiagnosticsCloudUsageMetric))]
    private bool _isRuntimeDiagnosticsEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeLoggingStateText))]
    [NotifyPropertyChangedFor(nameof(ToggleRuntimeLoggingLabel))]
    private bool _isRuntimeLoggingEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitoringStateText))]
    [NotifyPropertyChangedFor(nameof(MonitoringHelpText))]
    [NotifyPropertyChangedFor(nameof(ShowMonitoringProcessLoad))]
    [NotifyPropertyChangedFor(nameof(HasMonitoringProcessHistory))]
    private bool _isMonitoringEnabled;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRuntimeDiagnosticsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRuntimeLoggingCommand))]
    private bool _isRuntimeObservabilityBusy;
    [ObservableProperty] private string _runtimeObservabilityMessage = string.Empty;
    [ObservableProperty] private string _monitoringMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositorySyncIssues))]
    private int _repositorySyncIssueCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncQueueSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasRunnableSyncQueueWork))]
    [NotifyPropertyChangedFor(nameof(HasAnySyncQueueState))]
    private int _syncQueuePendingCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncQueueSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasRunnableSyncQueueWork))]
    [NotifyPropertyChangedFor(nameof(HasAnySyncQueueState))]
    private int _syncQueueRunningCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncQueueSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasRunnableSyncQueueWork))]
    [NotifyPropertyChangedFor(nameof(HasAnySyncQueueState))]
    private int _syncQueueRetryCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncQueueSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasRunnableSyncQueueWork))]
    [NotifyPropertyChangedFor(nameof(HasAnySyncQueueState))]
    private int _syncQueueAttentionCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSensitiveActionVerification))]
    [NotifyPropertyChangedFor(nameof(ShowConnectivityBanner))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerAccentColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerBackgroundColor))]
    [NotifyPropertyChangedFor(nameof(ConnectivityBannerText))]
    [NotifyPropertyChangedFor(nameof(CanSyncToCloud))]
    private bool _hasActiveProfile;
    [ObservableProperty] private bool _requirePasswordForSensitiveActions;
    [ObservableProperty] private bool _isWindowsAutostartEnabled;
    [ObservableProperty] private string _windowsAutostartCommandText = string.Empty;
    [ObservableProperty] private bool _isAutostartBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomaticSnapshotsSummaryText))]
    private bool _isAutomaticSnapshotsEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomaticSnapshotsSummaryText))]
    private int _selectedAutomaticSnapshotIntervalMinutes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomaticSnapshotsSummaryText))]
    private int _selectedAutomaticSnapshotQuietStartHour;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomaticSnapshotsSummaryText))]
    private int _selectedAutomaticSnapshotQuietEndHour;
    [ObservableProperty] private bool _isAutomaticSnapshotsBusy;
    [ObservableProperty] private string _automaticSnapshotsMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private bool _globalRetentionEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private string _globalRetentionMaxAgeDays = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private string _globalRetentionMaxSnapshots = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private string _globalRetentionMaxTotalSizeMb = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private string _globalRetentionTriggerFilter = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private int _selectedGlobalRetentionTriggerPresetIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalRetentionSummaryText))]
    private int _globalRetentionRunIntervalMinutes = 60;
    [ObservableProperty] private bool _isGlobalRetentionBusy;
    [ObservableProperty] private string _globalRetentionMessage = string.Empty;
    [ObservableProperty] private bool _isTransientCacheCleanupBusy;
    [ObservableProperty] private string _transientCacheCleanupMessage = string.Empty;
    [ObservableProperty] private bool _isTransientActionBusy;
    [ObservableProperty] private string _transientActionTitle = string.Empty;
    [ObservableProperty] private string _transientActionDetail = string.Empty;
    [ObservableProperty] private bool _isArtifactEncryptionEnabled;
    [ObservableProperty] private string _artifactEncryptionStatusText = string.Empty;
    [ObservableProperty] private string _artifactEncryptionActiveKeyText = string.Empty;
    [ObservableProperty] private string _artifactEncryptionUpdatedText = string.Empty;
    [ObservableProperty] private string _artifactEncryptionMessage = string.Empty;
    [ObservableProperty] private bool _isArtifactEncryptionBusy;

    [ObservableProperty]
    private AppLanguageOptionItemViewModel? _selectedLanguage;

    [ObservableProperty]
    private AppThemeOptionItemViewModel? _selectedTheme;

    [ObservableProperty]
    private AppExperienceOptionItemViewModel? _selectedExperienceMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationIssues))]
    private int _localizationResourceFilesCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationIssues))]
    private int _localizationTotalKeysCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationIssues))]
    private int _localizationMissingKeysCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationIssues))]
    private int _localizationDuplicateKeysCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationIssues))]
    private int _localizationExtraKeysCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationMissingSample))]
    private string _localizationMissingSample = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationDuplicateSample))]
    private string _localizationDuplicateSample = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalizationExtraSample))]
    private string _localizationExtraSample = string.Empty;

    [ObservableProperty]
    private string _localizationHealthText = string.Empty;

    public event Action? BackRequested;
    public event Func<int, Task>? OpenRepositorySettingsRequested;
    public event Func<string, Task>? ExperienceModeRefreshRequested;

    public ObservableCollection<AppSettingsTabViewModel> Tabs { get; } =
    [
        new("general", "app_settings.tab_general_title", "app_settings.tab_general_subtitle"),
        new("user", "app_settings.tab_user_title", "app_settings.tab_user_subtitle"),
        new("automation", "app_settings.tab_automation_title", "app_settings.tab_automation_subtitle"),
        new("sync", "app_settings.tab_sync_title", "app_settings.tab_sync_subtitle"),
        new("monitoring", "app_settings.tab_monitoring_title", "app_settings.tab_monitoring_subtitle")
    ];

    public ObservableCollection<AppUserProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<AppLanguageOptionItemViewModel> Languages { get; } = [];
    public ObservableCollection<AppThemeOptionItemViewModel> Themes { get; } = [];
    public ObservableCollection<AppExperienceOptionItemViewModel> ExperienceModes { get; } = [];
    public ObservableCollection<AppOperationJournalItemViewModel> OperationJournalItems { get; } = [];
    public ObservableCollection<AppRepositorySyncIssueItemViewModel> RepositorySyncIssues { get; } = [];
    public ObservableCollection<AppArtifactKeyItemViewModel> ArtifactKeys { get; } = [];
    public ObservableCollection<int> AutomaticSnapshotIntervalOptions { get; } = [5, 10, 15, 30, 60, 120, 180, 360, 720];
    public ObservableCollection<int> QuietHourOptions { get; } = new(Enumerable.Range(0, 24));

    public AppSettingsViewModel(
        IUserProfileRepository userProfiles,
        ILocalCredentialStore localCredentialStore,
        IAccessTokenPolicyService tokenPolicy,
        IAuthService auth,
        IOperationJournalService journal,
        IAppDiagnosticsService appDiagnostics,
        IRepositoryRetentionService retention,
        IRepositoryCloudSyncOrchestrator sync,
        ICloudSyncService cloudSyncService,
        ILocalBlockStorageMetricsService localStorageMetrics,
        IRetentionDefaultsStore retentionDefaultsStore,
        IAppTransientStateMaintenanceService transientStateMaintenance,
        OnboardingStateService onboardingState,
        ISensitiveActionGuard sensitiveActionGuard,
        IWindowService windows,
        IWindowsAutostartService autostart,
        IConnectivityStatusService connectivity,
        ICloudSyncRuntimeControlService cloudSyncRuntime,
        IMonitoringControlService monitoringControl,
        IProcessResourceStatusStore processResourceStatusStore,
        ISnapshotScheduler snapshotScheduler,
        ISnapshotSchedulerSettingsStore snapshotSchedulerSettingsStore,
        IRuntimeObservabilityControlService runtimeObservability,
        SnapshotSchedulerOptions snapshotSchedulerOptions,
        IServiceScopeExecutor scopeExecutor,
        IMediator mediator,
        IConfiguration config,
        ILogger<AppSettingsViewModel> log)
    {
        _userProfiles = userProfiles;
        _localCredentialStore = localCredentialStore;
        _tokenPolicy = tokenPolicy;
        _auth = auth;
        _journal = journal;
        _appDiagnostics = appDiagnostics;
        _retention = retention;
        _sync = sync;
        _cloudSyncService = cloudSyncService;
        _localStorageMetrics = localStorageMetrics;
        _retentionDefaultsStore = retentionDefaultsStore;
        _transientStateMaintenance = transientStateMaintenance;
        _onboardingState = onboardingState;
        _sensitiveActionGuard = sensitiveActionGuard;
        _windows = windows;
        _autostart = autostart;
        _connectivity = connectivity;
        _cloudSyncRuntime = cloudSyncRuntime;
        _monitoringControl = monitoringControl;
        _processResourceStatusStore = processResourceStatusStore;
        _snapshotScheduler = snapshotScheduler;
        _snapshotSchedulerSettingsStore = snapshotSchedulerSettingsStore;
        _runtimeObservability = runtimeObservability;
        _snapshotSchedulerOptions = snapshotSchedulerOptions;
        _scopeExecutor = scopeExecutor;
        _mediator = mediator;
        _log = log;
        _localization = LocalizationManager.Instance;
        _theme = ThemeManager.Instance;
        _experience = UserExperienceManager.Instance;

        CloudApiBaseUrl = config["CloudApi:BaseUrl"]
                          ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                          ?? "http://localhost:8080";
        IsArtifactEncryptionEnabled = config.GetValue<bool?>("Security:ArtifactEncryption:Enabled") ?? false;

        TokenPolicyHint = LocalizeUserFacingMessage(_tokenPolicy.GetPolicySummary(), "common.not_available_short");
        ClearCloudStorageMetrics();
        ClearLocalStorageMetrics();
        ClearSystemDiagnostics();
        WindowsAutostartCommandText = Loc.T("common.not_available_short");
        LoadAutomaticSnapshotSettings();
        ApplyGlobalRetentionDefaults(_retentionDefaultsStore.Load());
        ArtifactEncryptionStatusText = IsArtifactEncryptionEnabled
            ? Loc.T("app_settings.artifact_encryption_enabled")
            : Loc.T("app_settings.artifact_encryption_disabled");
        ArtifactEncryptionActiveKeyText = Loc.T("common.not_available_short");
        ArtifactEncryptionUpdatedText = Loc.T("common.not_available_short");
        ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
        ApplyMonitoringEnabledState(_monitoringControl.IsEnabled);
        SelectedTab = Tabs.FirstOrDefault();

        _localization.LanguageChanged += OnLanguageChanged;
        _theme.ThemeChanged += OnThemeChanged;
        _experience.ModeChanged += OnExperienceModeChanged;
        _connectivity.StatusChanged += OnConnectivityStatusChanged;
        _cloudSyncRuntime.StateChanged += OnCloudSyncRuntimeStateChanged;
        _monitoringControl.StateChanged += OnMonitoringStateChanged;
        _processResourceStatusStore.StatusChanged += OnProcessResourceStatusChanged;
        RebuildLanguageOptions();
        RebuildThemeOptions();
        RebuildExperienceOptions();
        UpdateTabVisibility();
        UpdateLocalizationDiagnostics();
    }

    public bool IsGeneralTabSelected => string.Equals(SelectedTab?.Key, "general", StringComparison.OrdinalIgnoreCase);
    public bool IsUserTabSelected => string.Equals(SelectedTab?.Key, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsAutomationTabSelected => string.Equals(SelectedTab?.Key, "automation", StringComparison.OrdinalIgnoreCase);
    public bool IsSyncTabSelected => string.Equals(SelectedTab?.Key, "sync", StringComparison.OrdinalIgnoreCase);
    public bool IsMonitoringTabSelected => string.Equals(SelectedTab?.Key, "monitoring", StringComparison.OrdinalIgnoreCase);
    public bool ShowSimpleAutoSnapshotsSection => IsBasicMode;
    public bool ShowInitialLoadingOverlay => IsLoading && !HasLoadedPrimaryState;
    public bool ShowRefreshActivityCard => IsLoading && HasLoadedPrimaryState;
    public string RefreshActivityTitle => Loc.T("app_settings.loading_title");
    public string RefreshActivityDetail => SelectedTabKey switch
    {
        "sync" => Loc.T("app_settings.sync_running_detail"),
        "monitoring" => Loc.T("app_settings.system_diagnostics_running_detail"),
        "automation" => Loc.T("app_settings.loading_detail"),
        "user" => Loc.T("app_settings.loading_detail"),
        _ => Loc.T("app_settings.loading_detail")
    };

    public bool HasLocalizationIssues => LocalizationMissingKeysCount > 0 || LocalizationDuplicateKeysCount > 0 || LocalizationExtraKeysCount > 0;
    public bool HasLocalizationMissingSample => !string.IsNullOrWhiteSpace(LocalizationMissingSample);
    public bool HasLocalizationDuplicateSample => !string.IsNullOrWhiteSpace(LocalizationDuplicateSample);
    public bool HasLocalizationExtraSample => !string.IsNullOrWhiteSpace(LocalizationExtraSample);
    public bool CanEditSensitiveActionVerification => HasActiveProfile;
    public bool HasRepositorySyncIssues => RepositorySyncIssueCount > 0;
    public bool HasRunnableSyncQueueWork => (SyncQueuePendingCount + SyncQueueRetryCount) > 0;
    public bool HasAnySyncQueueState => (SyncQueuePendingCount + SyncQueueRunningCount + SyncQueueRetryCount + SyncQueueAttentionCount) > 0;
    public bool HasKnownProfiles => Profiles.Count > 0;
    public bool IsGuestMode => !HasActiveProfile;
    public bool HasOperationJournalItems => OperationJournalItems.Count > 0;
    public bool HasArtifactKeys => ArtifactKeys.Count > 0;
    public int ArtifactActiveKeyCount => ArtifactKeys.Count(key => key.IsActive);
    public int ArtifactRetiredKeyCount => ArtifactKeys.Count(key => key.IsRetired);
    public int ArtifactRevokedKeyCount => ArtifactKeys.Count(key => key.IsRevoked);
    public bool IsBasicMode => _experience.IsBasicMode;
    public bool IsProfessionalMode => _experience.IsProfessionalMode;
    public bool ShowLocalizationDiagnostics => IsProfessionalMode;
    public bool ShowTechnicalCloudDetails => IsProfessionalMode && HasActiveProfile;
    public bool ShowTechnicalProfileDetails => IsProfessionalMode;
    public bool ShowCloudStorageDiagnostics => IsProfessionalMode && HasActiveProfile;
    public bool ShowDetailedRepositorySyncIssueDiagnostics => IsProfessionalMode;
    public bool ShowRetentionSection => true;
    public bool ShowTransientCacheSection => IsProfessionalMode;
    public bool ShowArtifactEncryptionSection => IsProfessionalMode && IsArtifactEncryptionEnabled;
    public bool ShowLocalStorageSection => IsProfessionalMode;
    public bool ShowSystemDiagnosticsSection => IsProfessionalMode;
    public bool ShowSystemDiagnosticsCloudUsageMetric => HasSystemDiagnostics && IsRuntimeDiagnosticsEnabled;
    public string MonitoringStateText => IsMonitoringEnabled
        ? Loc.T("app_settings.runtime_observability_state_enabled")
        : Loc.T("app_settings.runtime_observability_state_disabled");
    public string MonitoringHelpText => IsMonitoringEnabled
        ? Loc.T("app_settings.monitoring_enable_hint")
        : Loc.T("app_settings.monitoring_disabled_hint");
    public bool ShowMonitoringProcessLoad => IsMonitoringEnabled && _monitoringProcessResourceSnapshot is not null;
    public bool HasMonitoringProcessHistory => IsMonitoringEnabled && _processResourceStatusStore.History.Count > 0;
    public string MonitoringProcessLoadSummaryText
        => _monitoringProcessResourceSnapshot is not { } snapshot
            ? string.Empty
            : snapshot.CpuPercent.HasValue
                ? Loc.F(
                    "dashboard.process_load_summary",
                    snapshot.CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture),
                    FormatBytes(snapshot.WorkingSetBytes))
                : Loc.F("dashboard.process_load_summary_cpu_pending", FormatBytes(snapshot.WorkingSetBytes));
    public string MonitoringProcessLoadDetailText
        => _monitoringProcessResourceSnapshot is not { } snapshot
            ? string.Empty
            : Loc.F(
                "dashboard.process_load_detail",
                FormatBytes(snapshot.PrivateMemoryBytes),
                FormatBytes(snapshot.ManagedHeapBytes),
                snapshot.ThreadCount,
                snapshot.HandleCount,
                snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    public string MonitoringProcessLoadPeakText
        => ProcessResourceStatusPresenter.FormatPeakSummary(_processResourceStatusStore.History);
    public string MonitoringProcessLoadHistoryText
        => ProcessResourceStatusPresenter.FormatRecentHistory(_processResourceStatusStore.History);
    public string MonitoringProcessLoadAccentColor => ProcessResourceStatusPresenter.DescribeVisuals(_monitoringProcessResourceSnapshot).AccentColor;
    public string MonitoringProcessLoadBackgroundColor => ProcessResourceStatusPresenter.DescribeVisuals(_monitoringProcessResourceSnapshot).BackgroundColor;
    public bool CanConfigureWindowsAutostart => _autostart.IsSupported;
    public string AutomaticSnapshotsSummaryText => !IsAutomaticSnapshotsEnabled
        ? Loc.T("app_settings.auto_snapshots_summary_disabled")
        : Loc.F(
            "app_settings.auto_snapshots_summary_enabled",
            SelectedAutomaticSnapshotIntervalMinutes,
            FormatHourRange(SelectedAutomaticSnapshotQuietStartHour),
            FormatHourRange(SelectedAutomaticSnapshotQuietEndHour));
    public string GlobalRetentionSummaryText => !GlobalRetentionEnabled
        ? Loc.T("app_settings.global_retention_summary_disabled")
        : Loc.F(
            "app_settings.global_retention_summary_enabled",
            string.IsNullOrWhiteSpace(GlobalRetentionMaxAgeDays) ? Loc.T("common.not_available_short") : GlobalRetentionMaxAgeDays,
            string.IsNullOrWhiteSpace(GlobalRetentionMaxSnapshots) ? Loc.T("common.not_available_short") : GlobalRetentionMaxSnapshots,
            string.IsNullOrWhiteSpace(GlobalRetentionMaxTotalSizeMb) ? Loc.T("common.not_available_short") : GlobalRetentionMaxTotalSizeMb,
            GlobalRetentionRunIntervalMinutes);
    public string ExperienceModeHint => IsBasicMode
        ? Loc.T("app_settings.experience_basic_hint")
        : Loc.T("app_settings.experience_professional_hint");
    public string LocalizationSummaryText => HasLocalizationIssues
        ? Loc.T("app_settings.localization_user_warning")
        : Loc.T("app_settings.localization_user_ok");
    public string SyncSectionIntroText => IsGuestMode
        ? Loc.T("app_settings.sync_intro_guest")
        : IsBasicMode
            ? Loc.T("app_settings.sync_intro_basic")
            : Loc.T("app_settings.sync_intro_professional");
    public string SyncSectionHelpText => IsGuestMode
        ? Loc.T("app_settings.sync_help_guest")
        : IsBasicMode
            ? Loc.T("app_settings.sync_help_basic")
            : Loc.T("app_settings.sync_help_professional");
    public string CloudStorageHelpText => IsBasicMode
        ? Loc.T("app_settings.cloud_storage_help_basic")
        : Loc.T("app_settings.cloud_storage_help_professional");
    public string LocalStorageHelpText => IsBasicMode
        ? Loc.T("app_settings.local_storage_help_basic")
        : Loc.T("app_settings.local_storage_help_professional");
    public string SystemDiagnosticsHelpText => IsBasicMode
        ? Loc.T("app_settings.system_diagnostics_help_basic")
        : Loc.T("app_settings.system_diagnostics_help_professional");
    public bool CanToggleRuntimeObservability => !IsRuntimeObservabilityBusy;
    public string RuntimeDiagnosticsStateText => IsRuntimeDiagnosticsEnabled
        ? Loc.T("app_settings.runtime_observability_state_enabled")
        : Loc.T("app_settings.runtime_observability_state_disabled");
    public string RuntimeLoggingStateText => IsRuntimeLoggingEnabled
        ? Loc.T("app_settings.runtime_observability_state_enabled")
        : Loc.T("app_settings.runtime_observability_state_disabled");
    public string ToggleRuntimeDiagnosticsLabel => IsRuntimeDiagnosticsEnabled
        ? Loc.T("app_settings.runtime_diagnostics_disable_button")
        : Loc.T("app_settings.runtime_diagnostics_enable_button");
    public string ToggleRuntimeLoggingLabel => IsRuntimeLoggingEnabled
        ? Loc.T("app_settings.runtime_logging_disable_button")
        : Loc.T("app_settings.runtime_logging_enable_button");
    public string RepositorySyncHealthHelpText => IsBasicMode
        ? Loc.T("app_settings.repository_sync_health_help_basic")
        : Loc.T("app_settings.repository_sync_health_help_professional");
    public string RepositorySyncProgressHelpText => IsBasicMode
        ? Loc.T("app_settings.repository_sync_health_progress_help_basic")
        : Loc.T("app_settings.repository_sync_health_progress_help_professional");
    public string RestoreFromCloudLabel => IsBasicMode
        ? Loc.T("app_settings.restore_from_cloud_basic")
        : Loc.T("app_settings.restore_from_cloud");
    public string PushAllRepositoriesLabel => IsBasicMode
        ? Loc.T("app_settings.push_all_latest_basic")
        : Loc.T("app_settings.push_all_latest");
    public string ProcessQueueLabel => IsBasicMode
        ? Loc.T("app_settings.process_queue_basic")
        : Loc.T("app_settings.process_queue");
    public string SyncQueuePurposeText => Loc.T("app_settings.sync_queue_purpose");
    public string SyncQueueSummaryText => !HasAnySyncQueueState
        ? Loc.T("app_settings.sync_queue_summary_empty")
        : HasRunnableSyncQueueWork
            ? Loc.F(
                "app_settings.sync_queue_summary_active",
                SyncQueuePendingCount,
                SyncQueueRunningCount,
                SyncQueueRetryCount,
                SyncQueueAttentionCount)
            : SyncQueueRunningCount > 0
                ? Loc.F("app_settings.sync_queue_summary_running_only", SyncQueueRunningCount, SyncQueueAttentionCount)
                : Loc.F("app_settings.sync_queue_summary_attention_only", SyncQueueAttentionCount);
    public string ArtifactEncryptionCoverageSummary => Loc.T("app_settings.artifact_key_management_coverage_summary");

    public bool IsConfirmPasswordVisible => IsRegisterMode;
    public string SubmitAuthLabel => IsRegisterMode ? Loc.T("app_settings.auth_create_account") : Loc.T("auth.sign_in");
    public string ToggleAuthLabel => IsRegisterMode ? Loc.T("app_settings.auth_switch_to_sign_in") : Loc.T("app_settings.auth_switch_to_registration");

    public bool CanSubmitAuth
    {
        get
        {
            if (IsAuthBusy || string.IsNullOrWhiteSpace(UsernameInput) || string.IsNullOrWhiteSpace(PasswordInput))
                return false;

            if (!IsRegisterMode)
                return true;

            if (string.IsNullOrWhiteSpace(EmailInput) || !IsValidEmail(EmailInput))
                return false;

            return !string.IsNullOrWhiteSpace(ConfirmPasswordInput) &&
                   string.Equals(PasswordInput, ConfirmPasswordInput, StringComparison.Ordinal);
        }
    }

    partial void OnSelectedLanguageChanged(AppLanguageOptionItemViewModel? value)
    {
        if (_suppressLanguageSelectionChanged || value is null)
            return;

        _ = ApplyLanguageChangeAsync(value);
    }

    partial void OnSelectedThemeChanged(AppThemeOptionItemViewModel? value)
    {
        if (_suppressThemeSelectionChanged || value is null)
            return;

        if (!_theme.SetTheme(value.Code))
            SyncSelectedThemeOption(rebuildIfMissing: true);
    }

    partial void OnSelectedExperienceModeChanged(AppExperienceOptionItemViewModel? value)
    {
        if (_suppressExperienceSelectionChanged || value is null)
            return;

        _ = ChangeExperienceModeAsync(value);
    }

    partial void OnRequirePasswordForSensitiveActionsChanged(bool value)
    {
        if (_suppressSensitiveActionToggleChanged)
            return;

        _ = SaveSensitiveActionVerificationSettingAsync(value);
    }

    partial void OnIsMonitoringEnabledChanged(bool value)
    {
        NotifyMonitoringStateChanged();

        if (_suppressMonitoringEnabledChange)
            return;

        _ = SetMonitoringEnabledAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanToggleRuntimeObservability))]
    private Task ToggleRuntimeDiagnosticsAsync()
        => SetRuntimeDiagnosticsAsync(!IsRuntimeDiagnosticsEnabled);

    [RelayCommand(CanExecute = nameof(CanToggleRuntimeObservability))]
    private Task ToggleRuntimeLoggingAsync()
        => SetRuntimeLoggingAsync(!IsRuntimeLoggingEnabled);

    private void ApplyRuntimeObservabilitySnapshot(RuntimeObservabilitySnapshot snapshot)
    {
        IsRuntimeDiagnosticsEnabled = snapshot.IsDiagnosticsEnabled;
        IsRuntimeLoggingEnabled = snapshot.IsLoggingEnabled;
    }

    private async Task SetMonitoringEnabledAsync(bool enabled)
    {
        try
        {
            MonitoringMessage = string.Empty;
            await _monitoringControl.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update repository monitoring state");
            MonitoringMessage = Loc.T("app_settings.monitoring_enable_save_failed");
            ApplyMonitoringEnabledState(_monitoringControl.IsEnabled);
        }
    }

    private async Task SetRuntimeDiagnosticsAsync(bool enabled)
    {
        if (IsRuntimeObservabilityBusy)
            return;

        try
        {
            IsRuntimeObservabilityBusy = true;
            RuntimeObservabilityMessage = string.Empty;

            await _runtimeObservability.SetDiagnosticsEnabledAsync(enabled);
            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            RuntimeObservabilityMessage = Loc.T(enabled
                ? "app_settings.runtime_diagnostics_enabled_message"
                : "app_settings.runtime_diagnostics_disabled_message");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to change runtime diagnostics state. Enabled {Enabled}", enabled);
            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            RuntimeObservabilityMessage = Loc.T("app_settings.runtime_observability_save_failed");
        }
        finally
        {
            IsRuntimeObservabilityBusy = false;
        }
    }

    private async Task SetRuntimeLoggingAsync(bool enabled)
    {
        if (IsRuntimeObservabilityBusy)
            return;

        try
        {
            IsRuntimeObservabilityBusy = true;
            RuntimeObservabilityMessage = string.Empty;

            await _runtimeObservability.SetLoggingEnabledAsync(enabled);
            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            RuntimeObservabilityMessage = Loc.T(enabled
                ? "app_settings.runtime_logging_enabled_message"
                : "app_settings.runtime_logging_disabled_message");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to change runtime logging state. Enabled {Enabled}", enabled);
            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            RuntimeObservabilityMessage = Loc.T("app_settings.runtime_observability_save_failed");
        }
        finally
        {
            IsRuntimeObservabilityBusy = false;
        }
    }

    public async Task LoadAsync()
    {
        var (requestId, ct) = BeginLoadRequest();

        try
        {
            IsLoading = true;
            AuthMessage = string.Empty;
            SyncMessage = string.Empty;
            GeneralMessage = string.Empty;
            LocalStorageMessage = string.Empty;
            SystemDiagnosticsMessage = string.Empty;
            RuntimeObservabilityMessage = string.Empty;
            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            _log.LogInformation("Loading app settings");
            await Task.Yield();

            var activeTask = ExecuteIsolatedAsync<IUserProfileRepository, UserProfileSessionDto?>(
                (profiles, token) => profiles.GetActiveProfileAsync(token),
                ct);
            var profilesTask = ExecuteIsolatedAsync<IUserProfileRepository, IReadOnlyList<UserProfileSessionDto>>(
                (profiles, token) => profiles.GetProfilesAsync(token),
                ct);
            var repositoriesTask = SendIsolatedAsync(new GetAllRepositoriesQuery(), ct);

            await Task.WhenAll(activeTask, profilesTask, repositoriesTask);
            if (!IsLatestLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            var active = await activeTask;
            _lastActiveProfile = active;
            var activeTokenState = _tokenPolicy.Evaluate(active?.AccessToken);
            HasActiveProfile = active is not null;
            OnPropertyChanged(nameof(IsGuestMode));

            ActiveUsername = active?.Username ?? Loc.T("app_settings.not_signed_in");
            ActiveEmail = string.IsNullOrWhiteSpace(active?.Email)
                ? Loc.T("common.not_available_short")
                : active.Email!;
            ActiveCloudUserText = active?.CloudUserId?.ToString() ?? Loc.T("common.not_available_short");
            ActiveSessionTokenState = LocalizeUserFacingMessage(activeTokenState.Description, "app_settings.no_token");

            _suppressSensitiveActionToggleChanged = true;
            try
            {
                RequirePasswordForSensitiveActions = active?.RequirePasswordForSensitiveActions ?? false;
            }
            finally
            {
                _suppressSensitiveActionToggleChanged = false;
            }

            var profiles = await profilesTask;
            Profiles.Clear();
            foreach (var p in profiles)
            {
                var tokenState = _tokenPolicy.Evaluate(p.AccessToken);
                var cloudUserText = p.CloudUserId?.ToString() ?? Loc.T("common.not_available_short");
                var emailText = string.IsNullOrWhiteSpace(p.Email)
                    ? Loc.T("common.not_available_short")
                    : p.Email!;
                Profiles.Add(new AppUserProfileItemViewModel(
                    p.Username,
                    emailText,
                    string.Equals(p.Username, active?.Username, StringComparison.OrdinalIgnoreCase),
                    !string.IsNullOrWhiteSpace(p.AccessToken),
                    p.LastLoginAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    cloudUserText,
                    Loc.F("app_settings.cloud_id_format", cloudUserText),
                    LocalizeUserFacingMessage(tokenState.Description, "app_settings.no_token")));
            }

            var repositories = await repositoriesTask;
            LocalRepositoryCount = repositories.Count;
            _hasActiveSyncWork = RefreshRepositorySyncIssues(repositories);
            UpdateSyncQueueState(repositories);
            UpdateSyncStatusAutoRefreshState();
            _ = RunDeferredSettingsLoadAsync(requestId, active, ct);

            _log.LogInformation(
                "App settings primary state loaded. Profiles {Profiles}. Repositories {Repositories}. SyncIssues {SyncIssues}",
                Profiles.Count,
                LocalRepositoryCount,
                RepositorySyncIssueCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load app settings state");
            SyncMessage = Loc.T("app_settings.error_load_state");
        }
        finally
        {
            if (IsLatestLoadRequest(requestId))
            {
                HasLoadedPrimaryState = true;
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task RunSystemDiagnosticsAsync()
    {
        try
        {
            IsSystemDiagnosticsBusy = true;
            SystemDiagnosticsMessage = string.Empty;

            var report = await _appDiagnostics.RunAsync();
            ApplySystemDiagnostics(report);
            SystemDiagnosticsMessage = Loc.T("app_settings.system_diagnostics_loaded");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run application diagnostics");
            ClearSystemDiagnostics();
            SystemDiagnosticsMessage = Loc.T("app_settings.system_diagnostics_failed");
        }
        finally
        {
            IsSystemDiagnosticsBusy = false;
        }
    }

    [RelayCommand]
    private Task ExportSystemDiagnosticsTextAsync() => ExportSystemDiagnosticsAsync(asJson: false);

    [RelayCommand]
    private Task ExportSystemDiagnosticsJsonAsync() => ExportSystemDiagnosticsAsync(asJson: true);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void ReplayGuidedTour()
    {
        _log.LogInformation("Guided tour replay requested from app settings");
        _onboardingState.RequestFirstRunTour();
        GeneralMessage = Loc.T("app_settings.guided_tour_replay_started");
    }

    [RelayCommand]
    private async Task ToggleWindowsAutostartAsync()
    {
        if (!_autostart.IsSupported)
        {
            GeneralMessage = Loc.T("app_settings.windows_autostart_not_supported");
            return;
        }

        try
        {
            IsAutostartBusy = true;
            GeneralMessage = string.Empty;

            if (IsWindowsAutostartEnabled)
                await _autostart.DisableAsync();
            else
                await _autostart.EnableAsync();

            await LoadWindowsAutostartStateAsync();
            GeneralMessage = IsWindowsAutostartEnabled
                ? Loc.T("app_settings.windows_autostart_enabled_message")
                : Loc.T("app_settings.windows_autostart_disabled_message");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to toggle Windows autostart");
            GeneralMessage = Loc.T("app_settings.windows_autostart_failed");
        }
        finally
        {
            IsAutostartBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAutomaticSnapshotsSettingsAsync()
    {
        try
        {
            IsAutomaticSnapshotsBusy = true;
            AutomaticSnapshotsMessage = string.Empty;

            var normalized = BuildSchedulerSettingsFromUi();
            await ApplySchedulerSettingsAsync(normalized);

            LoadAutomaticSnapshotSettings();
            AutomaticSnapshotsMessage = normalized.Enabled
                ? Loc.F("app_settings.auto_snapshots_saved_enabled", normalized.IntervalMinutes)
                : Loc.T("app_settings.auto_snapshots_saved_disabled");
            await AppendJournalAsync("info", "scheduler", "settings_auto_snapshots_updated", AutomaticSnapshotsMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save automatic snapshot settings");
            AutomaticSnapshotsMessage = Loc.T("app_settings.auto_snapshots_save_failed");
            await AppendJournalAsync("error", "scheduler", "settings_auto_snapshots_updated", $"{AutomaticSnapshotsMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsAutomaticSnapshotsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyReducedBackgroundLoadPresetAsync()
    {
        try
        {
            IsAutomaticSnapshotsBusy = true;
            AutomaticSnapshotsMessage = string.Empty;

            var current = BuildSchedulerSettingsFromUi();
            var integrityEnabled = current.IntegrityEnabled ?? _snapshotSchedulerOptions.IntegrityEnabled;
            var reduced = current with
            {
                IntervalMinutes = current.Enabled
                    ? Math.Max(current.IntervalMinutes, SnapshotSchedulerOptions.RecommendedIntervalMinutes)
                    : current.IntervalMinutes,
                PollSeconds = Math.Max(
                    NormalizeSchedulerPollSeconds(current.PollSeconds),
                    SnapshotSchedulerOptions.RecommendedPollSeconds),
                MaxReadBytesPerSecond = CapBackgroundReadRate(current.MaxReadBytesPerSecond),
                MaxIoOperationsPerSecond = CapBackgroundIops(current.MaxIoOperationsPerSecond),
                IntegrityEnabled = integrityEnabled,
                IntegrityIntervalMinutes = integrityEnabled
                    ? Math.Max(
                        NormalizeIntegrityIntervalMinutes(current.IntegrityIntervalMinutes),
                        SnapshotSchedulerOptions.RecommendedIntegrityIntervalMinutes)
                    : NormalizeIntegrityIntervalMinutes(current.IntegrityIntervalMinutes)
            };

            await ApplySchedulerSettingsAsync(reduced);

            if (IsRuntimeLoggingEnabled)
                await _runtimeObservability.SetLoggingEnabledAsync(false);

            ApplyRuntimeObservabilitySnapshot(_runtimeObservability.Snapshot);
            LoadAutomaticSnapshotSettings();

            AutomaticSnapshotsMessage = Loc.T("app_settings.auto_snapshots_low_impact_applied");
            await AppendJournalAsync("info", "scheduler", "settings_low_impact_mode_applied", AutomaticSnapshotsMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to apply reduced background load preset");
            AutomaticSnapshotsMessage = Loc.T("app_settings.auto_snapshots_low_impact_failed");
            await AppendJournalAsync("error", "scheduler", "settings_low_impact_mode_applied", $"{AutomaticSnapshotsMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsAutomaticSnapshotsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveGlobalRetentionDefaultsAsync()
    {
        try
        {
            IsGlobalRetentionBusy = true;
            GlobalRetentionMessage = string.Empty;

            var settings = BuildGlobalRetentionDefaults();
            await _retentionDefaultsStore.SaveAsync(settings);

            GlobalRetentionMessage = Loc.T("app_settings.global_retention_defaults_saved");
            await AppendJournalAsync("info", "retention", "settings_global_retention_defaults_saved", GlobalRetentionMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save global retention defaults");
            GlobalRetentionMessage = Loc.T("app_settings.global_retention_defaults_save_failed");
            await AppendJournalAsync("error", "retention", "settings_global_retention_defaults_saved", $"{GlobalRetentionMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsGlobalRetentionBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyGlobalRetentionDefaultsToAllAsync()
    {
        try
        {
            IsGlobalRetentionBusy = true;
            GlobalRetentionMessage = string.Empty;

            var settings = BuildGlobalRetentionDefaults();
            await _retentionDefaultsStore.SaveAsync(settings);

            if (!settings.Enabled)
            {
                GlobalRetentionMessage = GlobalRetentionSummaryText;
                return;
            }

            var policy = settings.ToPolicy();

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            var targetRepositories = repositories
                .Where(static repository => !repository.RetentionPolicy.HasLocalOverride)
                .ToList();
            var updated = 0;

            foreach (var repository in targetRepositories)
            {
                var detail = await _mediator.Send(new GetRepositoryDetailQuery(repository.Id));
                if (detail is null)
                    continue;

                var effectiveTriggers = policy.TriggerFilters.Count == 0
                    ? [SafeDefaultRetentionTriggerFilter]
                    : policy.TriggerFilters;

                var effectivePolicy = new RepositoryRetentionPolicyDto(
                    Enabled: detail.RetentionPolicy.Enabled,
                    MaxAgeDays: policy.MaxAgeDays,
                    MaxSnapshots: policy.MaxSnapshots,
                    MaxTotalSizeBytes: policy.MaxTotalSizeBytes,
                    TriggerFilters: effectiveTriggers,
                    RunIntervalMinutes: policy.RunIntervalMinutes,
                    MaintenanceWindowStartHour: detail.RetentionPolicy.MaintenanceWindowStartHour,
                    MaintenanceWindowEndHour: detail.RetentionPolicy.MaintenanceWindowEndHour,
                    LastRunAtUtc: detail.RetentionPolicy.LastRunAtUtc,
                    LastStatus: detail.RetentionPolicy.LastStatus,
                    StorageMode: policy.StorageMode,
                    HasLocalOverride: false,
                    PolicySource: RepositoryRetentionPolicySources.Global);

                var result = await _mediator.Send(new UpdateRepositoryConfigurationCommand(
                    detail.Id,
                    detail.Name,
                    detail.Description,
                    detail.DirectoryPath,
                    detail.LinkedFormats,
                    detail.AutoCaptureFileVersions,
                    detail.ProtectCloudMetadata,
                    detail.ExcludedPatterns,
                    effectivePolicy));

                if (result.Success)
                    updated++;
            }

            GlobalRetentionMessage = Loc.F("app_settings.global_retention_applied_to_all", updated);
            await AppendJournalAsync("info", "retention", "settings_global_retention_apply_all", GlobalRetentionMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to apply global retention defaults to all repositories");
            GlobalRetentionMessage = Loc.T("app_settings.global_retention_apply_failed");
            await AppendJournalAsync("error", "retention", "settings_global_retention_apply_all", $"{GlobalRetentionMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsGlobalRetentionBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunGlobalRetentionCleanupNowAsync()
    {
        try
        {
            IsGlobalRetentionBusy = true;
            GlobalRetentionMessage = string.Empty;

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            var retentionEnabledRepositories = repositories.ToList();

            var results = new List<RepositoryRetentionRunResultDto>(retentionEnabledRepositories.Count);
            foreach (var repository in retentionEnabledRepositories)
            {
                var result = await _retention.RunRetentionAsync(repository.Id, dryRun: false);
                results.Add(result);
            }

            var affected = results.Count(result => result.PolicyApplied);
            var snapshots = results.Sum(result => result.SnapshotsMarked);
            var versions = results.Sum(result => result.FileVersionsMarked);
            var blocks = results.Sum(result => result.BlockFilesDeleted);
            var freedBytes = results.Sum(result => result.EstimatedFreedBytes);
            GlobalRetentionMessage = Loc.F(
                "app_settings.global_retention_cleanup_done_detail",
                affected,
                results.Count,
                snapshots,
                versions,
                blocks,
                FormatBytes(freedBytes));
            await AppendJournalAsync("info", "retention", "settings_global_retention_cleanup_now", GlobalRetentionMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to run global retention cleanup");
            GlobalRetentionMessage = Loc.T("app_settings.global_retention_cleanup_failed");
            await AppendJournalAsync("error", "retention", "settings_global_retention_cleanup_now", $"{GlobalRetentionMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsGlobalRetentionBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearTransientStateCacheAsync()
    {
        try
        {
            IsTransientCacheCleanupBusy = true;
            TransientCacheCleanupMessage = string.Empty;

            var result = await _transientStateMaintenance.ClearAsync();
            TransientCacheCleanupMessage = Loc.F(
                "app_settings.transient_cache_cleanup_done",
                result.DeletedFiles,
                result.DeletedDirectories,
                result.RootPath);
            await AppendJournalAsync("info", "maintenance", "settings_transient_cache_cleanup", TransientCacheCleanupMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to clear transient state cache");
            TransientCacheCleanupMessage = Loc.T("app_settings.transient_cache_cleanup_failed");
            await AppendJournalAsync("error", "maintenance", "settings_transient_cache_cleanup", $"{TransientCacheCleanupMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsTransientCacheCleanupBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshArtifactEncryptionAsync()
    {
        await ReloadArtifactEncryptionStateAsync();
    }

    [RelayCommand]
    private async Task RefreshLocalStorageMetricsAsync()
    {
        await LoadLocalStorageMetricsAsync(silent: false);
    }

    [RelayCommand]
    private async Task RotateArtifactKeyAsync()
    {
        await RotateArtifactKeyWithNoteAsync(null);
    }

    [RelayCommand]
    private async Task RevokeArtifactKeyAsync(AppArtifactKeyItemViewModel? key)
    {
        await RevokeArtifactKeyWithNoteAsync(key, null);
    }

    public async Task ReloadArtifactEncryptionStateAsync()
    {
        await LoadArtifactEncryptionStateAsync();
    }

    public async Task RefreshSyncHealthAsync()
    {
        await RefreshSyncSectionAsync(silentMetrics: false, refreshStorageMetrics: false);
    }

    public async Task RotateArtifactKeyWithNoteAsync(string? note)
    {
        if (!IsArtifactEncryptionEnabled)
        {
            ArtifactEncryptionMessage = Loc.T("app_settings.artifact_encryption_disabled");
            return;
        }

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_rotate_artifact_key",
                "security.action_rotate_artifact_key_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ArtifactEncryptionMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsArtifactEncryptionBusy = true;
            ArtifactEncryptionMessage = string.Empty;
            var result = await _mediator.Send(new RotateArtifactKeyCommand(string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            if (!result.Success)
            {
                ArtifactEncryptionMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "app_settings.artifact_encryption_rotate_failed");
                return;
            }

            await LoadArtifactEncryptionStateAsync();
            ArtifactEncryptionMessage = Loc.F("app_settings.artifact_encryption_rotate_success", result.Value?.KeyId ?? string.Empty);
            await AppendJournalAsync("info", "security", "settings_rotate_artifact_key", ArtifactEncryptionMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to rotate artifact encryption key");
            ArtifactEncryptionMessage = Loc.T("app_settings.artifact_encryption_rotate_failed");
            await AppendJournalAsync("error", "security", "settings_rotate_artifact_key", $"{ArtifactEncryptionMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsArtifactEncryptionBusy = false;
        }
    }

    public async Task RevokeArtifactKeyWithNoteAsync(AppArtifactKeyItemViewModel? key, string? note)
    {
        if (key is null || !key.CanRevoke)
            return;

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_revoke_artifact_key",
                "security.action_revoke_artifact_key_body",
                [key.KeyId]);

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    ArtifactEncryptionMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsArtifactEncryptionBusy = true;
            ArtifactEncryptionMessage = string.Empty;
            var result = await _mediator.Send(new RevokeArtifactKeyCommand(
                key.KeyId,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            if (!result.Success)
            {
                ArtifactEncryptionMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "app_settings.artifact_encryption_revoke_failed");
                return;
            }

            await LoadArtifactEncryptionStateAsync();
            ArtifactEncryptionMessage = Loc.F("app_settings.artifact_encryption_revoke_success", key.KeyId);
            await AppendJournalAsync("warning", "security", "settings_revoke_artifact_key", ArtifactEncryptionMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to revoke artifact encryption key {KeyId}", key.KeyId);
            ArtifactEncryptionMessage = Loc.T("app_settings.artifact_encryption_revoke_failed");
            await AppendJournalAsync("error", "security", "settings_revoke_artifact_key", $"{ArtifactEncryptionMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsArtifactEncryptionBusy = false;
        }
    }

    public void SelectTabByKey(string key)
    {
        var tab = Tabs.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
            SelectTab(tab);
    }

    public Task ShowCloudSyncHealthCenterAsync()
        => OpenCloudSyncHealthCenterCoreAsync();

    public Task OpenRepositorySyncIssueSettingsDirectAsync(AppRepositorySyncIssueItemViewModel? issue)
        => OpenRepositorySyncIssueSettingsCoreAsync(issue);

    public Task RetryRepositorySyncIssueDirectAsync(AppRepositorySyncIssueItemViewModel? issue)
        => RetryRepositorySyncIssueCoreAsync(issue);

    public Task CancelRepositorySyncIssueDirectAsync(AppRepositorySyncIssueItemViewModel? issue)
        => CancelRepositorySyncIssueCoreAsync(issue);

    [RelayCommand]
    private async Task OpenRepositorySyncIssueSettingsAsync(AppRepositorySyncIssueItemViewModel? issue)
        => await OpenRepositorySyncIssueSettingsCoreAsync(issue);

    private async Task OpenRepositorySyncIssueSettingsCoreAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null || OpenRepositorySettingsRequested is null)
            return;

        _log.LogInformation("Opening repository settings from sync issues. RepositoryId {RepositoryId}", issue.RepositoryId);
        await OpenRepositorySettingsRequested.Invoke(issue.RepositoryId);
    }

    [RelayCommand]
    private async Task RetryRepositorySyncIssueAsync(AppRepositorySyncIssueItemViewModel? issue)
        => await RetryRepositorySyncIssueCoreAsync(issue);

    private async Task RetryRepositorySyncIssueCoreAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null)
            return;

        try
        {
            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                SyncMessage = connectivityMessage;
                return;
            }

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_retry_repository_sync_body",
                [issue.Name]);

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }
            OnPropertyChanged(nameof(HasKnownProfiles));

            IsSyncBusy = true;
            SyncMessage = string.Empty;

            _log.LogInformation(
                "Manual retry requested from sync issues. RepositoryId {RepositoryId}. Name {RepositoryName}",
                issue.RepositoryId,
                issue.Name);

            await _sync.TryPushLatestSnapshotAsync(issue.RepositoryId);

            SyncMessage = Loc.F("app_settings.sync_retry_repository_requested", issue.Name);
            await AppendJournalAsync("info", "sync", "settings_retry_repository_sync", SyncMessage, ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Manual retry for repository sync failed. RepositoryId {RepositoryId}. Name {RepositoryName}",
                issue.RepositoryId,
                issue.Name);
            SyncMessage = Loc.F("app_settings.sync_retry_repository_failed", issue.Name);
            await AppendJournalAsync("error", "sync", "settings_retry_repository_sync", $"{SyncMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelRepositorySyncIssueAsync(AppRepositorySyncIssueItemViewModel? issue)
        => await CancelRepositorySyncIssueCoreAsync(issue);

    private async Task CancelRepositorySyncIssueCoreAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null)
            return;

        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_cancel_repository_sync_body",
                [issue.Name]);

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;

            _log.LogInformation(
                "Manual cancellation requested from sync issues. RepositoryId {RepositoryId}. Name {RepositoryName}",
                issue.RepositoryId,
                issue.Name);

            var cancelled = await _sync.CancelRepositorySyncAsync(issue.RepositoryId);
            SyncMessage = cancelled
                ? Loc.F("app_settings.sync_cancel_repository_requested", issue.Name)
                : Loc.F("app_settings.sync_cancel_repository_failed", issue.Name);

            await AppendJournalAsync(
                cancelled ? "info" : "warning",
                "sync",
                "settings_cancel_repository_sync",
                SyncMessage,
                ActiveUsername);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Manual cancellation for repository sync failed. RepositoryId {RepositoryId}. Name {RepositoryName}",
                issue.RepositoryId,
                issue.Name);
            SyncMessage = Loc.F("app_settings.sync_cancel_repository_failed", issue.Name);
            await AppendJournalAsync("error", "sync", "settings_cancel_repository_sync", $"{SyncMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private void SelectTab(AppSettingsTabViewModel? tab)
    {
        if (tab is not null && tab.IsVisible)
            SelectedTab = tab;
    }

    partial void OnSelectedTabChanged(AppSettingsTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsSelected = ReferenceEquals(tab, value);

        UpdateSyncStatusAutoRefreshState();
        OnPropertyChanged(nameof(RefreshActivityDetail));
        OnPropertyChanged(nameof(ShowConnectivityBanner));
        OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
        OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
        OnPropertyChanged(nameof(ConnectivityBannerText));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitialLoadingOverlay));
        OnPropertyChanged(nameof(ShowRefreshActivityCard));
        OnPropertyChanged(nameof(RefreshActivityTitle));
        OnPropertyChanged(nameof(RefreshActivityDetail));
    }

    partial void OnHasLoadedPrimaryStateChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitialLoadingOverlay));
        OnPropertyChanged(nameof(ShowRefreshActivityCard));
    }

    private void UpdateTabVisibility()
    {
        var automationTab = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.Key, "automation", StringComparison.OrdinalIgnoreCase));
        var monitoringTab = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.Key, "monitoring", StringComparison.OrdinalIgnoreCase));

        if (automationTab is not null)
            automationTab.IsVisible = true;
        if (monitoringTab is not null)
            monitoringTab.IsVisible = IsProfessionalMode;

        if (SelectedTab is not null && !SelectedTab.IsVisible)
        {
            SelectedTab = Tabs.FirstOrDefault(tab =>
                string.Equals(tab.Key, "general", StringComparison.OrdinalIgnoreCase) && tab.IsVisible)
                ?? Tabs.FirstOrDefault(tab => tab.IsVisible);
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
            await Task.Yield();
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

    [RelayCommand]
    private void ToggleAuthMode()
    {
        IsRegisterMode = !IsRegisterMode;
        AuthMessage = string.Empty;
    }

    [RelayCommand]
    private Task OpenSignInDialogAsync() => OpenAuthDialogAsync(registerMode: false);

    [RelayCommand]
    private Task OpenRegisterDialogAsync() => OpenAuthDialogAsync(registerMode: true);

    [RelayCommand]
    private Task AddUserAsync() => OpenAuthDialogAsync(registerMode: false);

    [RelayCommand(CanExecute = nameof(CanSubmitAuth))]
    private async Task SubmitAuthAsync()
    {
        try
        {
            IsAuthBusy = true;
            AuthMessage = string.Empty;
            _log.LogInformation(
                "Submitting auth request from app settings. Mode {Mode}. Username {Username}",
                IsRegisterMode ? "register" : "login",
                UsernameInput.Trim());

            if (IsRegisterMode && !string.Equals(PasswordInput, ConfirmPasswordInput, StringComparison.Ordinal))
            {
                AuthMessage = Loc.T("app_settings.auth_passwords_mismatch");
                return;
            }

            var username = UsernameInput.Trim();
            var email = EmailInput.Trim();
            var session = IsRegisterMode
                ? await _auth.RegisterAsync(username, email, PasswordInput)
                : await _auth.LoginAsync(username, PasswordInput);

            if (session is null)
            {
                AuthMessage = IsRegisterMode
                    ? Loc.T("app_settings.auth_registration_failed")
                    : Loc.T("app_settings.auth_login_failed");

                await AppendJournalAsync(
                    "warning",
                    "auth",
                    IsRegisterMode ? "settings_register" : "settings_login",
                    AuthMessage,
                    UsernameInput.Trim());
                return;
            }

            await _userProfiles.SaveOrUpdateProfileAsync(
                session.Username,
                session.CloudUserId,
                session.AccessToken,
                session.Email,
                session.CloudSessionId,
                session.RefreshToken,
                session.AccessTokenExpiresAtUtc,
                session.RefreshTokenExpiresAtUtc);
            await _localCredentialStore.SavePasswordAsync(session.Username, PasswordInput);

            var restored = 0;
            var cloudSyncFailed = false;
            try
            {
                restored = await _sync.RestoreRepositoriesFromCloudAsync();
                await _sync.ProcessPendingQueueAsync();
            }
            catch (Exception cloudEx)
            {
                cloudSyncFailed = true;
                _log.LogWarning(cloudEx, "Cloud follow-up after auth is unavailable. Username {Username}", session.Username);
            }

            PasswordInput = string.Empty;
            ConfirmPasswordInput = string.Empty;
            if (IsRegisterMode)
                EmailInput = string.Empty;

            var tokenText = session.AccessTokenExpiresAtUtc.HasValue
                ? Loc.F("app_settings.auth_token_expires", session.AccessTokenExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

            var cloudNote = cloudSyncFailed ? Loc.T("app_settings.auth_success_cloud_unavailable") : tokenText;

            AuthMessage = IsRegisterMode
                ? Loc.F("app_settings.auth_registration_success", restored, cloudNote)
                : Loc.F("app_settings.auth_login_success", restored, cloudNote);

            await AppendJournalAsync(
                "info",
                "auth",
                IsRegisterMode ? "settings_register" : "settings_login",
                AuthMessage,
                session.Username);

            await LoadAsync();
            _log.LogInformation(
                "Auth request completed successfully in app settings. Mode {Mode}. Username {Username}",
                IsRegisterMode ? "register" : "login",
                session.Username);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auth submit failed in app settings");

            if (!IsRegisterMode)
            {
                var normalizedUsername = UsernameInput.Trim();
                if (await _localCredentialStore.VerifyPasswordAsync(normalizedUsername, PasswordInput) &&
                    await _userProfiles.SetActiveProfileAsync(normalizedUsername))
                {
                    AuthMessage = Loc.F("app_settings.profile_switched_without_token", normalizedUsername);
                    await LoadAsync();
                    return;
                }
            }

            AuthMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "app_settings.auth_request_failed");
            await AppendJournalAsync(
                "error",
                "auth",
                IsRegisterMode ? "settings_register" : "settings_login",
                $"{AuthMessage} {ex.Message}",
                UsernameInput.Trim());
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    [RelayCommand]
    private async Task ActivateProfileAsync(AppUserProfileItemViewModel? profile)
    {
        if (profile is null)
            return;

        if (profile.IsActive)
        {
            _log.LogInformation("Profile activation skipped because profile is already active. Username {Username}", profile.Username);
            AuthMessage = Loc.T("app_settings.profile_already_active");
            return;
        }

        try
        {
            IsAuthBusy = true;
            AuthMessage = string.Empty;
            _log.LogInformation("Activating local profile. Username {Username}", profile.Username);

            var switched = await _userProfiles.SetActiveProfileAsync(profile.Username);
            if (!switched)
            {
                _log.LogWarning("Failed to activate profile because it was not found. Username {Username}", profile.Username);
                AuthMessage = Loc.T("app_settings.profile_not_found");
                return;
            }

            try
            {
                await _sync.ProcessPendingQueueAsync();
            }
            catch (Exception syncEx)
            {
                _log.LogWarning(syncEx, "Cloud queue resume skipped while activating profile {Username}", profile.Username);
            }
            AuthMessage = profile.HasAccessToken
                ? Loc.F("app_settings.profile_switched", profile.Username)
                : Loc.F("app_settings.profile_switched_without_token", profile.Username);
            await LoadAsync();
            _log.LogInformation("Local profile activated. Username {Username}. HasAccessToken {HasAccessToken}", profile.Username, profile.HasAccessToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to activate profile {Username}", profile.Username);
            AuthMessage = Loc.T("app_settings.profile_switch_failed");
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            IsAuthBusy = true;
            _log.LogInformation("Signing out active profile");
            var activeProfile = await _userProfiles.GetActiveProfileAsync();
            if (activeProfile is null)
            {
                _log.LogInformation("Sign-out skipped because there is no active profile");
                AuthMessage = Loc.T("app_settings.sign_out_not_signed_in");
                return;
            }

            if (!string.IsNullOrWhiteSpace(activeProfile?.AccessToken))
            {
                var activeUsername = activeProfile.Username;
                try
                {
                    var cloudLoggedOut = await _auth.LogoutAsync(activeProfile.AccessToken);
                    if (!cloudLoggedOut)
                        _log.LogInformation("Cloud logout returned non-success for user {Username}", activeUsername);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cloud logout failed for user {Username}; continuing local sign-out", activeUsername);
                }
            }

            var signedOutUsername = activeProfile?.Username ?? ActiveUsername;
            await _userProfiles.SignOutActiveAsync();
            AuthMessage = Loc.T("app_settings.signed_out");
            await AppendJournalAsync("info", "auth", "settings_sign_out", AuthMessage, ActiveUsername);
            await LoadAsync();
            _log.LogInformation("Active profile signed out. Username {Username}", signedOutUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sign-out failed");
            AuthMessage = Loc.T("app_settings.sign_out_failed");
            await AppendJournalAsync("error", "auth", "settings_sign_out", $"{AuthMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowOperationJournalAsync()
    {
        await RunTransientActionAsync(
            "operation_journal.loading_title",
            "operation_journal.loading_detail",
            async () =>
            {
                var owner = _windows.GetActiveWindow();
                var window = _windows.Create<OperationJournalWindow>();
                if (window.DataContext is not OperationJournalWindowViewModel vm)
                    return;

                await vm.LoadAsync();

                if (owner is not null)
                    await _windows.ShowDialogAsync(window, owner);
                else
                    _windows.Show(window);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open operation journal window");
                GeneralMessage = Loc.T("app_settings.operation_journal_open_failed");
            });
    }

    [RelayCommand]
    private async Task OpenOperationMonitorAsync()
    {
        await RunTransientActionAsync(
            "app_settings.operation_monitor_loading_title",
            "app_settings.operation_monitor_loading_detail",
            () =>
            {
                var window = _windows.Create<OperationMonitorWindow>();
                _windows.Show(window);
                return Task.CompletedTask;
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open operation monitor window");
                GeneralMessage = Loc.T("app_settings.operation_monitor_open_failed");
            });
    }

    [RelayCommand]
    private async Task OpenCloudSyncHealthCenterAsync()
        => await OpenCloudSyncHealthCenterCoreAsync();

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

    private async Task OpenCloudSyncHealthCenterCoreAsync()
    {
        await RunTransientActionAsync(
            "app_settings.sync_health_center_loading_title",
            "app_settings.sync_health_center_loading_detail",
            async () =>
            {
                await RefreshSyncHealthAsync();

                var owner = _windows.GetActiveWindow();
                var window = _windows.Create<CloudSyncHealthWindow>();
                window.DataContext = new CloudSyncHealthWindowViewModel(this);

                if (owner is not null)
                    await _windows.ShowDialogAsync(window, owner);
                else
                    _windows.Show(window);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open cloud sync health center");
                SyncMessage = Loc.T("app_settings.sync_health_center_open_failed");
            });
    }

    [RelayCommand]
    private async Task OpenArtifactKeyManagementAsync()
    {
        await RunTransientActionAsync(
            "app_settings.artifact_key_management_loading_title",
            "app_settings.artifact_key_management_loading_detail",
            async () =>
            {
                await ReloadArtifactEncryptionStateAsync();

                var owner = _windows.GetActiveWindow();
                var window = new ArtifactKeyManagementWindow
                {
                    DataContext = new ArtifactKeyManagementWindowViewModel(this)
                };

                if (owner is not null)
                    await _windows.ShowDialogAsync(window, owner);
                else
                    _windows.Show(window);
            },
            ex =>
            {
                _log.LogError(ex, "Failed to open artifact key management window");
                GeneralMessage = Loc.T("app_settings.artifact_key_management_open_failed");
            });
    }

    [RelayCommand]
    private async Task RestoreFromCloudAsync()
    {
        try
        {
            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                SyncMessage = connectivityMessage;
                return;
            }

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_cloud_restore_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            var owner = _windows.GetActiveWindow();
            if (owner is null)
            {
                SyncMessage = Loc.T("repo_settings.error_picker_unavailable");
                return;
            }

            var folder = await owner.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    AllowMultiple = false,
                    Title = Loc.T("app_settings.restore_from_cloud_pick_folder")
                });

            var targetRoot = StoragePathResolver.TryGetLocalPath(folder.FirstOrDefault());
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                SyncMessage = Loc.T("app_settings.restore_from_cloud_cancelled");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;
            await AppendJournalAsync("info", "sync", "settings_restore_from_cloud", "Operation started.", ActiveUsername);

            var restored = await _sync.RestoreRepositoriesFromCloudAsync(targetRoot);
            SyncMessage = Loc.F("app_settings.sync_restore_finished_with_path", restored, targetRoot);
            await AppendJournalAsync("info", "sync", "settings_restore_from_cloud", SyncMessage, ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cloud restore failed");
            SyncMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "app_settings.sync_restore_failed");
            await AppendJournalAsync("error", "sync", "settings_restore_from_cloud", $"{SyncMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProcessQueueAsync()
    {
        try
        {
            var repositories = await SendIsolatedAsync(new GetAllRepositoriesQuery());
            UpdateSyncQueueState(repositories);

            if (!HasRunnableSyncQueueWork)
            {
                SyncMessage = SyncQueueRunningCount > 0
                    ? Loc.F("app_settings.sync_queue_already_running", SyncQueueRunningCount)
                    : SyncQueueAttentionCount > 0
                        ? Loc.F("app_settings.sync_queue_attention_only", SyncQueueAttentionCount)
                        : Loc.T("app_settings.sync_queue_nothing_to_process");
                return;
            }

            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                SyncMessage = connectivityMessage;
                return;
            }

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_process_queue_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;
            await AppendJournalAsync("info", "sync", "settings_process_queue", "Operation started.", ActiveUsername);

            await _sync.ProcessPendingQueueAsync();
            await LoadAsync();
            SyncMessage = HasRunnableSyncQueueWork
                ? Loc.F("app_settings.sync_queue_processed_with_remaining", SyncQueuePendingCount + SyncQueueRetryCount)
                : SyncQueueAttentionCount > 0
                    ? Loc.F("app_settings.sync_queue_processed_attention_remaining", SyncQueueAttentionCount)
                    : Loc.T("app_settings.sync_queue_processed");
            await AppendJournalAsync("info", "sync", "settings_process_queue", SyncMessage, ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Processing sync queue failed");
            SyncMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "app_settings.sync_queue_failed");
            await AppendJournalAsync("error", "sync", "settings_process_queue", $"{SyncMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task PushAllRepositoriesAsync()
    {
        try
        {
            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                SyncMessage = connectivityMessage;
                return;
            }

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_push_all_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;
            await AppendJournalAsync("info", "sync", "settings_push_all", "Operation started.", ActiveUsername);

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            foreach (var repository in repositories)
                await _sync.TryPushLatestSnapshotAsync(repository.Id);

            await _sync.ProcessPendingQueueAsync();

            SyncMessage = Loc.F("app_settings.sync_push_requested", repositories.Count);
            await AppendJournalAsync("info", "sync", "settings_push_all", SyncMessage, ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Push all repositories to cloud failed");
            SyncMessage = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "app_settings.sync_push_failed");
            await AppendJournalAsync("error", "sync", "settings_push_all", $"{SyncMessage} {ex.Message}", ActiveUsername);
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCloudStorageMetricsAsync()
    {
        try
        {
            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                CloudStorageMessage = connectivityMessage;
                return;
            }

            IsCloudMaintenanceBusy = true;
            CloudStorageMessage = string.Empty;
            _log.LogInformation("Refreshing cloud storage metrics and sync section status");
            await AppendJournalAsync("info", "sync", "settings_cloud_storage_refresh", "Operation started.", ActiveUsername);

            await Task.WhenAll(
                RefreshSyncSectionAsync(silentMetrics: false, refreshStorageMetrics: false),
                RefreshCloudStorageMetricsOnlyAsync(silent: false));
            _log.LogInformation(
                "Cloud storage refresh finished. HasMetrics {HasMetrics}. SyncIssues {SyncIssues}. ActiveWork {ActiveWork}",
                HasCloudStorageMetrics,
                RepositorySyncIssueCount,
                _hasActiveSyncWork);
            await AppendJournalAsync(
                "info",
                "sync",
                "settings_cloud_storage_refresh",
                string.IsNullOrWhiteSpace(CloudStorageMessage)
                    ? Loc.T("app_settings.cloud_storage_metrics_loaded")
                    : CloudStorageMessage,
                ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to refresh sync section status");
            CloudStorageMessage = Loc.T("app_settings.cloud_storage_metrics_failed");
            await AppendJournalAsync(
                "error",
                "sync",
                "settings_cloud_storage_refresh",
                $"{CloudStorageMessage} {ex.Message}",
                ActiveUsername);
        }
        finally
        {
            IsCloudMaintenanceBusy = false;
        }
    }

    private async Task RefreshCloudStorageMetricsOnlyAsync(bool silent, CancellationToken ct = default)
    {
        var active = await ExecuteIsolatedAsync<IUserProfileRepository, UserProfileSessionDto?>(
            (profiles, token) => profiles.GetActiveProfileAsync(token),
            ct);
        _lastActiveProfile = active;
        await LoadCloudStorageMetricsAsync(active, silent, ct);
    }

    [RelayCommand]
    private async Task RepairCloudStorageAsync()
    {
        try
        {
            if (ResolveCloudConnectivityMessage() is { } connectivityMessage)
            {
                CloudStorageMessage = connectivityMessage;
                return;
            }

            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_storage_repair",
                "security.action_cloud_storage_repair_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    CloudStorageMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsCloudMaintenanceBusy = true;
            CloudStorageMessage = string.Empty;
            _log.LogInformation("Running cloud storage repair");
            await AppendJournalAsync("info", "sync", "settings_cloud_storage_repair", "Operation started.", ActiveUsername);

            var active = await _userProfiles.GetActiveProfileAsync();
            if (!TryResolveCloudAccessToken(active, silent: false, out var accessToken))
                return;

            var result = await _cloudSyncService.RepairStorageAsync(accessToken);
            if (result is null)
            {
                _log.LogWarning("Cloud storage repair returned no result");
                CloudStorageMessage = Loc.T("app_settings.cloud_storage_repair_failed");
                return;
            }

            ApplyCloudStorageMetrics(result.Metrics);
            CloudStorageMessage = Loc.F(
                "app_settings.cloud_storage_repair_finished",
                result.Repair.Compacted,
                result.Repair.MissingMarked,
                result.Repair.BrokenPackRefs + result.Repair.BrokenLooseRefs);

            await AppendJournalAsync(
                "info",
                "sync",
                "settings_cloud_storage_repair",
                CloudStorageMessage,
                ActiveUsername);

            await RefreshSyncSectionAsync(silentMetrics: true, refreshStorageMetrics: false);

            _log.LogInformation(
                "Cloud storage repair finished. Compacted {Compacted}. MissingMarked {MissingMarked}. BrokenReferences {BrokenReferences}",
                result.Repair.Compacted,
                result.Repair.MissingMarked,
                result.Repair.BrokenPackRefs + result.Repair.BrokenLooseRefs);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogInformation(ex, "Cloud storage repair requires re-authentication");
            ClearCloudStorageMetrics();
            CloudStorageMessage = LocalizeUserFacingMessage(ex.Message, "app_settings.cloud_storage_sign_in_required");
            await AppendJournalAsync(
                "error",
                "sync",
                "settings_cloud_storage_repair",
                CloudStorageMessage,
                ActiveUsername);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cloud storage repair failed");
            CloudStorageMessage = Loc.T("app_settings.cloud_storage_repair_failed");
            await AppendJournalAsync(
                "error",
                "sync",
                "settings_cloud_storage_repair",
                $"{CloudStorageMessage} {ex.Message}",
                ActiveUsername);
        }
        finally
        {
            IsCloudMaintenanceBusy = false;
        }
    }

    private async Task LoadWindowsAutostartStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_autostart.IsSupported)
        {
            IsWindowsAutostartEnabled = false;
            WindowsAutostartCommandText = Loc.T("app_settings.windows_autostart_not_supported");
            return;
        }

        var isEnabledTask = _autostart.IsEnabledAsync();
        var commandTask = _autostart.GetRegisteredCommandAsync();
        await Task.WhenAll(isEnabledTask, commandTask);
        ct.ThrowIfCancellationRequested();

        IsWindowsAutostartEnabled = await isEnabledTask;
        WindowsAutostartCommandText = await commandTask ?? Loc.T("common.not_available_short");
    }

    private void LoadAutomaticSnapshotSettings()
    {
        IsAutomaticSnapshotsEnabled = _snapshotSchedulerOptions.Enabled;
        SelectedAutomaticSnapshotIntervalMinutes = Math.Clamp(_snapshotSchedulerOptions.IntervalMinutes, 1, 24 * 60);
        SelectedAutomaticSnapshotQuietStartHour = Math.Clamp(_snapshotSchedulerOptions.QuietHoursStartHour, 0, 23);
        SelectedAutomaticSnapshotQuietEndHour = Math.Clamp(_snapshotSchedulerOptions.QuietHoursEndHour, 0, 23);
        OnPropertyChanged(nameof(AutomaticSnapshotsSummaryText));
    }

    private SnapshotSchedulerUserSettings BuildSchedulerSettingsFromUi()
    {
        return new SnapshotSchedulerUserSettings(
            Enabled: IsAutomaticSnapshotsEnabled,
            IntervalMinutes: Math.Clamp(SelectedAutomaticSnapshotIntervalMinutes, 1, 24 * 60),
            QuietHoursStartHour: Math.Clamp(SelectedAutomaticSnapshotQuietStartHour, 0, 23),
            QuietHoursEndHour: Math.Clamp(SelectedAutomaticSnapshotQuietEndHour, 0, 23),
            PollSeconds: NormalizeSchedulerPollSeconds(_snapshotSchedulerOptions.PollSeconds),
            MaxReadBytesPerSecond: Math.Max(0, _snapshotSchedulerOptions.MaxReadBytesPerSecond),
            MaxIoOperationsPerSecond: Math.Max(0, _snapshotSchedulerOptions.MaxIoOperationsPerSecond),
            IntegrityEnabled: _snapshotSchedulerOptions.IntegrityEnabled,
            IntegrityIntervalMinutes: NormalizeIntegrityIntervalMinutes(_snapshotSchedulerOptions.IntegrityIntervalMinutes));
    }

    private async Task ApplySchedulerSettingsAsync(SnapshotSchedulerUserSettings settings)
    {
        _snapshotSchedulerOptions.Enabled = settings.Enabled;
        _snapshotSchedulerOptions.IntervalMinutes = Math.Clamp(settings.IntervalMinutes, 1, 24 * 60);
        _snapshotSchedulerOptions.QuietHoursStartHour = Math.Clamp(settings.QuietHoursStartHour, 0, 23);
        _snapshotSchedulerOptions.QuietHoursEndHour = Math.Clamp(settings.QuietHoursEndHour, 0, 23);
        _snapshotSchedulerOptions.PollSeconds = NormalizeSchedulerPollSeconds(settings.PollSeconds);
        _snapshotSchedulerOptions.MaxReadBytesPerSecond = Math.Max(0, settings.MaxReadBytesPerSecond ?? SnapshotSchedulerOptions.RecommendedMaxReadBytesPerSecond);
        _snapshotSchedulerOptions.MaxIoOperationsPerSecond = Math.Max(0, settings.MaxIoOperationsPerSecond ?? SnapshotSchedulerOptions.RecommendedMaxIoOperationsPerSecond);
        _snapshotSchedulerOptions.IntegrityEnabled = settings.IntegrityEnabled ?? true;
        _snapshotSchedulerOptions.IntegrityIntervalMinutes = NormalizeIntegrityIntervalMinutes(settings.IntegrityIntervalMinutes);

        var normalized = new SnapshotSchedulerUserSettings(
            _snapshotSchedulerOptions.Enabled,
            _snapshotSchedulerOptions.IntervalMinutes,
            _snapshotSchedulerOptions.QuietHoursStartHour,
            _snapshotSchedulerOptions.QuietHoursEndHour,
            _snapshotSchedulerOptions.PollSeconds,
            _snapshotSchedulerOptions.MaxReadBytesPerSecond,
            _snapshotSchedulerOptions.MaxIoOperationsPerSecond,
            _snapshotSchedulerOptions.IntegrityEnabled,
            _snapshotSchedulerOptions.IntegrityIntervalMinutes);

        await _snapshotSchedulerSettingsStore.SaveAsync(normalized);
        await _snapshotScheduler.StopAsync();
        if (normalized.Enabled)
            _snapshotScheduler.Start();
    }

    private void OnMonitoringStateChanged(bool enabled)
    {
        Dispatcher.UIThread.Post(() => ApplyMonitoringEnabledState(enabled));
    }

    private void OnProcessResourceStatusChanged(object? sender, ProcessResourceStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _monitoringProcessResourceSnapshot = IsMonitoringEnabled ? e.Snapshot : null;
            NotifyMonitoringStateChanged();
        });
    }

    private void ApplyMonitoringEnabledState(bool enabled)
    {
        _suppressMonitoringEnabledChange = true;
        try
        {
            IsMonitoringEnabled = enabled;
        }
        finally
        {
            _suppressMonitoringEnabledChange = false;
        }

        _monitoringProcessResourceSnapshot = enabled
            ? _processResourceStatusStore.Snapshot
            : null;

        NotifyMonitoringStateChanged();
    }

    private static int NormalizeSchedulerPollSeconds(int? value)
        => Math.Clamp(value ?? SnapshotSchedulerOptions.RecommendedPollSeconds, 5, 600);

    private static int NormalizeIntegrityIntervalMinutes(int? value)
        => Math.Clamp(value ?? SnapshotSchedulerOptions.RecommendedIntegrityIntervalMinutes, 30, 7 * 24 * 60);

    private static int CapBackgroundReadRate(int? value)
    {
        if (value is null || value <= 0)
            return SnapshotSchedulerOptions.RecommendedMaxReadBytesPerSecond;

        return Math.Min(value.Value, SnapshotSchedulerOptions.RecommendedMaxReadBytesPerSecond);
    }

    private static int CapBackgroundIops(int? value)
    {
        if (value is null || value <= 0)
            return SnapshotSchedulerOptions.RecommendedMaxIoOperationsPerSecond;

        return Math.Min(value.Value, SnapshotSchedulerOptions.RecommendedMaxIoOperationsPerSecond);
    }

    private async Task LoadGlobalRetentionDefaultsAsync(CancellationToken ct = default)
    {
        ApplyGlobalRetentionDefaults(await _retentionDefaultsStore.LoadAsync(ct));
    }

    private void ApplyGlobalRetentionDefaults(RetentionDefaultsUserSettings? settings)
    {
        settings ??= new RetentionDefaultsUserSettings(
            Enabled: false,
            MaxAgeDays: 30,
            MaxSnapshots: 200,
            MaxTotalSizeBytes: 2L * 1024 * 1024 * 1024,
            TriggerFilter: null,
            RunIntervalMinutes: 60);

        GlobalRetentionEnabled = settings.Enabled;
        GlobalRetentionMaxAgeDays = settings.MaxAgeDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        GlobalRetentionMaxSnapshots = settings.MaxSnapshots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        GlobalRetentionMaxTotalSizeMb = settings.MaxTotalSizeBytes.HasValue
            ? Math.Max(1, settings.MaxTotalSizeBytes.Value / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        GlobalRetentionTriggerFilter = settings.TriggerFilter ?? string.Empty;
        SelectedGlobalRetentionTriggerPresetIndex = GetRetentionTriggerPresetIndex(GlobalRetentionTriggerFilter);
        GlobalRetentionRunIntervalMinutes = Math.Clamp(settings.RunIntervalMinutes, 5, 7 * 24 * 60);
        OnPropertyChanged(nameof(GlobalRetentionSummaryText));
    }

    private RetentionDefaultsUserSettings BuildGlobalRetentionDefaults()
    {
        var maxAge = ParseNullablePositiveInt(GlobalRetentionMaxAgeDays, 1, 3650);
        var maxSnapshots = ParseNullablePositiveInt(GlobalRetentionMaxSnapshots, 1, 100000);
        var maxTotalSizeMb = ParseNullablePositiveLong(GlobalRetentionMaxTotalSizeMb, 1, 10L * 1024 * 1024);
        var triggerFilter = string.IsNullOrWhiteSpace(GlobalRetentionTriggerFilter)
            ? GlobalRetentionEnabled
                ? SafeDefaultRetentionTriggerFilter
                : null
            : GlobalRetentionTriggerFilter.Trim();

        return new RetentionDefaultsUserSettings(
            Enabled: GlobalRetentionEnabled,
            MaxAgeDays: maxAge,
            MaxSnapshots: maxSnapshots,
            MaxTotalSizeBytes: maxTotalSizeMb.HasValue ? maxTotalSizeMb.Value * 1024 * 1024 : null,
            TriggerFilter: triggerFilter,
            RunIntervalMinutes: Math.Clamp(GlobalRetentionRunIntervalMinutes, 5, 7 * 24 * 60));
    }

    partial void OnGlobalRetentionEnabledChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(GlobalRetentionTriggerFilter))
            GlobalRetentionTriggerFilter = SafeDefaultRetentionTriggerFilter;

        OnPropertyChanged(nameof(GlobalRetentionSummaryText));
    }

    partial void OnGlobalRetentionTriggerFilterChanged(string value)
    {
        SelectedGlobalRetentionTriggerPresetIndex = GetRetentionTriggerPresetIndex(value);
        OnPropertyChanged(nameof(GlobalRetentionSummaryText));
    }

    partial void OnSelectedGlobalRetentionTriggerPresetIndexChanged(int value)
    {
        var nextFilter = BuildRetentionTriggerFilterFromPreset(value);
        if (string.Equals(GlobalRetentionTriggerFilter, nextFilter, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(GlobalRetentionSummaryText));
            return;
        }

        GlobalRetentionTriggerFilter = nextFilter;
    }

    private static int GetRetentionTriggerPresetIndex(string? value)
    {
        var (includeAutomatic, includeManual, includeWorking) = ParseRetentionTriggerFlags(value);
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

    private static string BuildRetentionTriggerFilterFromPreset(int value)
    {
        var triggers = value switch
        {
            1 => new[] { "automatic", "working" },
            2 => new[] { "automatic", "manual" },
            3 => new[] { "manual" },
            4 => new[] { "working" },
            5 => new[] { "manual", "working" },
            6 => new[] { "automatic", "manual", "working" },
            _ => new[] { SafeDefaultRetentionTriggerFilter }
        };

        return string.Join(", ", triggers);
    }

    private static (bool includeAutomatic, bool includeManual, bool includeWorking) ParseRetentionTriggerFlags(string? value)
    {
        var triggers = (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static trigger => !string.IsNullOrWhiteSpace(trigger))
            .Select(static trigger => trigger.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includeAll = triggers.Contains("all");
        return (
            includeAll || triggers.Contains("automatic") || triggers.Contains("auto"),
            includeAll || triggers.Contains("manual"),
            includeAll || triggers.Contains("working"));
    }

    private async Task LoadArtifactEncryptionStateAsync(CancellationToken ct = default)
    {
        ArtifactEncryptionStatusText = IsArtifactEncryptionEnabled
            ? Loc.T("app_settings.artifact_encryption_enabled")
            : Loc.T("app_settings.artifact_encryption_disabled");

        if (!IsArtifactEncryptionEnabled)
        {
            ArtifactKeys.Clear();
            OnPropertyChanged(nameof(HasArtifactKeys));
            OnPropertyChanged(nameof(ArtifactActiveKeyCount));
            OnPropertyChanged(nameof(ArtifactRetiredKeyCount));
            OnPropertyChanged(nameof(ArtifactRevokedKeyCount));
            ArtifactEncryptionActiveKeyText = Loc.T("common.not_available_short");
            ArtifactEncryptionUpdatedText = Loc.T("common.not_available_short");
            return;
        }

        try
        {
            var ring = await SendIsolatedAsync(new GetArtifactKeyRingQuery(), ct);
            ArtifactKeys.Clear();

            foreach (var key in ring.Keys
                         .OrderByDescending(item => item.CreatedAtUtc))
            {
                var status = ArtifactKeyStatus.Normalize(key.Status);
                var isActive = string.Equals(ring.ActiveKeyId, key.KeyId, StringComparison.OrdinalIgnoreCase);
                ArtifactKeys.Add(new AppArtifactKeyItemViewModel(
                    key.KeyId,
                    status,
                    LocalizeArtifactKeyStatus(status),
                    key.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    key.RotatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Loc.T("common.not_available_short"),
                    key.RevokedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Loc.T("common.not_available_short"),
                    string.IsNullOrWhiteSpace(key.Note) ? Loc.T("common.not_available_short") : key.Note!,
                    isActive,
                    !isActive && !string.Equals(status, ArtifactKeyStatus.Revoked, StringComparison.OrdinalIgnoreCase),
                    string.Equals(status, ArtifactKeyStatus.Retired, StringComparison.OrdinalIgnoreCase),
                    string.Equals(status, ArtifactKeyStatus.Revoked, StringComparison.OrdinalIgnoreCase),
                    key.RotatedAtUtc.HasValue,
                    key.RevokedAtUtc.HasValue));
            }

            ArtifactEncryptionActiveKeyText = string.IsNullOrWhiteSpace(ring.ActiveKeyId)
                ? Loc.T("common.not_available_short")
                : ring.ActiveKeyId;
            ArtifactEncryptionUpdatedText = ring.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            OnPropertyChanged(nameof(HasArtifactKeys));
            OnPropertyChanged(nameof(ArtifactActiveKeyCount));
            OnPropertyChanged(nameof(ArtifactRetiredKeyCount));
            OnPropertyChanged(nameof(ArtifactRevokedKeyCount));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load artifact encryption state");
            ArtifactKeys.Clear();
            OnPropertyChanged(nameof(HasArtifactKeys));
            OnPropertyChanged(nameof(ArtifactActiveKeyCount));
            OnPropertyChanged(nameof(ArtifactRetiredKeyCount));
            OnPropertyChanged(nameof(ArtifactRevokedKeyCount));
            ArtifactEncryptionActiveKeyText = Loc.T("common.not_available_short");
            ArtifactEncryptionUpdatedText = Loc.T("common.not_available_short");
            ArtifactEncryptionMessage = Loc.T("app_settings.artifact_encryption_load_failed");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshLocalizationState);
    }

    private async Task ApplyLanguageChangeAsync(AppLanguageOptionItemViewModel value)
    {
        if (_isLanguageRefreshInProgress
            || string.Equals(value.Code, _localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isLanguageRefreshInProgress = true;
        try
        {
            GeneralMessage = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            _localization.SetLanguage(value.Code);

            Dispatcher.UIThread.Post(() => SyncSelectedLanguageOption(rebuildIfMissing: true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to apply language change. Language {Language}", value.Code);
            GeneralMessage = Loc.T("common.error_generic");
            RebuildLanguageOptions();
        }
        finally
        {
            _isLanguageRefreshInProgress = false;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => SyncSelectedThemeOption(rebuildIfMissing: false));
    }

    partial void OnHasActiveProfileChanged(bool value)
        => _connectivity.SetCloudProbeEnabled(value);

    private void OnConnectivityStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowConnectivityBanner));
            OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
            OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
            OnPropertyChanged(nameof(ConnectivityBannerText));
            OnPropertyChanged(nameof(CanSyncToCloud));
        });
    }

    private void OnCloudSyncRuntimeStateChanged(CloudSyncRuntimeSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowConnectivityBanner));
            OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
            OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
            OnPropertyChanged(nameof(ConnectivityBannerText));
            OnPropertyChanged(nameof(CanSyncToCloud));
        });
    }

    private void OnExperienceModeChanged(object? sender, EventArgs e)
    {
        RebuildExperienceOptions();
        UpdateTabVisibility();
        OnPropertyChanged(nameof(IsBasicMode));
        OnPropertyChanged(nameof(IsProfessionalMode));
        OnPropertyChanged(nameof(IsAutomationTabSelected));
        OnPropertyChanged(nameof(ShowSimpleAutoSnapshotsSection));
        OnPropertyChanged(nameof(ShowLocalizationDiagnostics));
        OnPropertyChanged(nameof(ShowTechnicalCloudDetails));
        OnPropertyChanged(nameof(ShowTechnicalProfileDetails));
        OnPropertyChanged(nameof(ShowCloudStorageDiagnostics));
        OnPropertyChanged(nameof(ShowDetailedRepositorySyncIssueDiagnostics));
        OnPropertyChanged(nameof(ShowSystemDiagnosticsCloudUsageMetric));
        OnPropertyChanged(nameof(ShowRetentionSection));
        OnPropertyChanged(nameof(ShowTransientCacheSection));
        OnPropertyChanged(nameof(ShowArtifactEncryptionSection));
        OnPropertyChanged(nameof(ShowLocalStorageSection));
        OnPropertyChanged(nameof(ShowSystemDiagnosticsSection));
        OnPropertyChanged(nameof(ExperienceModeHint));
        OnPropertyChanged(nameof(LocalizationSummaryText));
        OnPropertyChanged(nameof(SyncSectionIntroText));
        OnPropertyChanged(nameof(SyncSectionHelpText));
        OnPropertyChanged(nameof(CloudStorageHelpText));
        OnPropertyChanged(nameof(LocalStorageHelpText));
        OnPropertyChanged(nameof(AutomaticSnapshotsSummaryText));
        OnPropertyChanged(nameof(GlobalRetentionSummaryText));
        OnPropertyChanged(nameof(SystemDiagnosticsHelpText));
        OnPropertyChanged(nameof(RepositorySyncHealthHelpText));
        OnPropertyChanged(nameof(RepositorySyncProgressHelpText));
        OnPropertyChanged(nameof(RestoreFromCloudLabel));
        OnPropertyChanged(nameof(PushAllRepositoriesLabel));
        OnPropertyChanged(nameof(ProcessQueueLabel));
        OnPropertyChanged(nameof(SyncQueuePurposeText));
        OnPropertyChanged(nameof(SyncQueueSummaryText));
        OnPropertyChanged(nameof(HasRunnableSyncQueueWork));
        OnPropertyChanged(nameof(HasAnySyncQueueState));
        OnPropertyChanged(nameof(ArtifactEncryptionCoverageSummary));
        OnPropertyChanged(nameof(CanConfigureWindowsAutostart));
        OnPropertyChanged(nameof(HasArtifactKeys));
        OnPropertyChanged(nameof(ArtifactActiveKeyCount));
        OnPropertyChanged(nameof(ArtifactRetiredKeyCount));
        OnPropertyChanged(nameof(ArtifactRevokedKeyCount));
        OnPropertyChanged(nameof(ShowConnectivityBanner));
        OnPropertyChanged(nameof(ConnectivityBannerAccentColor));
        OnPropertyChanged(nameof(ConnectivityBannerBackgroundColor));
        OnPropertyChanged(nameof(ConnectivityBannerText));
        _ = LoadOperationJournalAsync();
        _ = RefreshSyncSectionAsync(silentMetrics: true, refreshStorageMetrics: false, CancellationToken.None);
        _ = LoadWindowsAutostartStateAsync();
        _ = LoadArtifactEncryptionStateAsync();
    }

    private async Task ChangeExperienceModeAsync(AppExperienceOptionItemViewModel value)
    {
        if (IsExperienceModeChangeInProgress)
            return;

        if (string.Equals(value.Code, _experience.CurrentModeCode, StringComparison.OrdinalIgnoreCase))
            return;

        IsExperienceModeChangeInProgress = true;
        try
        {
            GeneralMessage = string.Empty;

            var confirmed = await ConfirmExperienceModeChangeAsync(value);
            if (!confirmed)
            {
                RebuildExperienceOptions();
                return;
            }

            if (!_experience.SetMode(value.Code))
            {
                RebuildExperienceOptions();
                GeneralMessage = Loc.T("app_settings.experience_apply_failed");
                return;
            }

            if (ExperienceModeRefreshRequested is not null)
                await ExperienceModeRefreshRequested.Invoke(value.Code);

            GeneralMessage = Loc.F("app_settings.experience_applied", value.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to change experience mode to {ModeCode}", value.Code);
            RebuildExperienceOptions();
            GeneralMessage = Loc.T("app_settings.experience_apply_failed");
        }
        finally
        {
            IsExperienceModeChangeInProgress = false;
        }
    }

    private async Task<bool> ConfirmExperienceModeChangeAsync(AppExperienceOptionItemViewModel value)
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return true;

        var window = _windows.Create<ConfirmActionWindow>();
        if (window.DataContext is ConfirmActionWindowViewModel vm)
        {
            var bodyKey = string.Equals(value.Code, "professional", StringComparison.OrdinalIgnoreCase)
                ? "app_settings.experience_confirm_body_professional"
                : "app_settings.experience_confirm_body_basic";

            vm.ConfigureLocalized(
                "app_settings.experience_confirm_title",
                bodyKey,
                null,
                "app_settings.experience_confirm_warning",
                "app_settings.experience_confirm_button");
        }

        await _windows.ShowDialogAsync(window, owner);
        return window.DataContext is ConfirmActionWindowViewModel resultVm && resultVm.IsConfirmed;
    }

    private void RefreshLocalizationState()
    {
        foreach (var tab in Tabs)
            tab.RefreshLocalization();

        if (!HasActiveProfile)
        {
            ActiveUsername = Loc.T("app_settings.not_signed_in");
            ActiveEmail = Loc.T("common.not_available_short");
            ActiveCloudUserText = Loc.T("common.not_available_short");
            ActiveSessionTokenState = Loc.T("app_settings.no_token");
        }

        TokenPolicyHint = LocalizeUserFacingMessage(_tokenPolicy.GetPolicySummary(), "common.not_available_short");
        RebuildLanguageOptions();
        RebuildThemeOptions();
        RebuildExperienceOptions();
        UpdateTabVisibility();
        UpdateLocalizationDiagnostics();
        ArtifactEncryptionStatusText = IsArtifactEncryptionEnabled
            ? Loc.T("app_settings.artifact_encryption_enabled")
            : Loc.T("app_settings.artifact_encryption_disabled");
        OnPropertyChanged(nameof(IsBasicMode));
        OnPropertyChanged(nameof(IsProfessionalMode));
        OnPropertyChanged(nameof(IsAutomationTabSelected));
        OnPropertyChanged(nameof(ShowSimpleAutoSnapshotsSection));
        OnPropertyChanged(nameof(ShowLocalizationDiagnostics));
        OnPropertyChanged(nameof(ShowTechnicalCloudDetails));
        OnPropertyChanged(nameof(ShowTechnicalProfileDetails));
        OnPropertyChanged(nameof(ShowCloudStorageDiagnostics));
        OnPropertyChanged(nameof(ShowDetailedRepositorySyncIssueDiagnostics));
        OnPropertyChanged(nameof(ShowRetentionSection));
        OnPropertyChanged(nameof(ShowTransientCacheSection));
        OnPropertyChanged(nameof(ShowArtifactEncryptionSection));
        OnPropertyChanged(nameof(ShowLocalStorageSection));
        OnPropertyChanged(nameof(ShowSystemDiagnosticsSection));
        OnPropertyChanged(nameof(ExperienceModeHint));
        OnPropertyChanged(nameof(LocalizationSummaryText));
        OnPropertyChanged(nameof(SyncSectionIntroText));
        OnPropertyChanged(nameof(SyncSectionHelpText));
        OnPropertyChanged(nameof(CloudStorageHelpText));
        OnPropertyChanged(nameof(LocalStorageHelpText));
        OnPropertyChanged(nameof(SystemDiagnosticsHelpText));
        OnPropertyChanged(nameof(AutomaticSnapshotsSummaryText));
        OnPropertyChanged(nameof(GlobalRetentionSummaryText));
        OnPropertyChanged(nameof(RepositorySyncHealthHelpText));
        OnPropertyChanged(nameof(RepositorySyncProgressHelpText));
        OnPropertyChanged(nameof(RestoreFromCloudLabel));
        OnPropertyChanged(nameof(PushAllRepositoriesLabel));
        OnPropertyChanged(nameof(ProcessQueueLabel));
        OnPropertyChanged(nameof(ArtifactEncryptionCoverageSummary));
        OnPropertyChanged(nameof(RuntimeDiagnosticsStateText));
        OnPropertyChanged(nameof(RuntimeLoggingStateText));
        OnPropertyChanged(nameof(MonitoringStateText));
        OnPropertyChanged(nameof(MonitoringHelpText));
        OnPropertyChanged(nameof(ToggleRuntimeDiagnosticsLabel));
        OnPropertyChanged(nameof(ToggleRuntimeLoggingLabel));
        NotifyMonitoringStateChanged();
        OnPropertyChanged(nameof(CanConfigureWindowsAutostart));
        OnPropertyChanged(nameof(HasArtifactKeys));
        OnPropertyChanged(nameof(ArtifactActiveKeyCount));
        OnPropertyChanged(nameof(ArtifactRetiredKeyCount));
        OnPropertyChanged(nameof(ArtifactRevokedKeyCount));

        OnPropertyChanged(nameof(SubmitAuthLabel));
        OnPropertyChanged(nameof(ToggleAuthLabel));
        _ = LoadWindowsAutostartStateAsync();
        _ = LoadArtifactEncryptionStateAsync();

        if (_lastCloudStorageMetrics is not null)
            ApplyCloudStorageMetrics(_lastCloudStorageMetrics);
        else if (!HasCloudStorageMetrics)
            ClearCloudStorageMetrics();

        if (_lastLocalStorageMetrics is not null)
            ApplyLocalStorageMetrics(_lastLocalStorageMetrics);
        else if (!HasLocalStorageMetrics)
            ClearLocalStorageMetrics();

        if (_lastDiagnosticsReport is not null)
            ApplySystemDiagnostics(_lastDiagnosticsReport);
        else
        {
            UpdateSystemDiagnosticsCloudStorageState();
            UpdateSystemDiagnosticsDedupState();
        }
    }

    public string SelectedTabKey => SelectedTab?.Key ?? "general";

    private async Task RefreshSyncSectionAsync(
        bool silentMetrics,
        bool refreshStorageMetrics,
        CancellationToken ct = default)
    {
        if (!await _syncStatusRefreshGate.WaitAsync(0, ct))
        {
            _log.LogDebug("Skipping sync section refresh because another refresh is already running");
            return;
        }

        try
        {
            UserProfileSessionDto? active = _lastActiveProfile;
            if (refreshStorageMetrics)
            {
                active = await ExecuteIsolatedAsync<IUserProfileRepository, UserProfileSessionDto?>(
                    (profiles, token) => profiles.GetActiveProfileAsync(token),
                    ct);
                _lastActiveProfile = active;
            }

            var repositories = await SendIsolatedAsync(new GetAllRepositoriesQuery(), ct);

            LocalRepositoryCount = repositories.Count;
            _hasActiveSyncWork = RefreshRepositorySyncIssues(repositories);
            UpdateSyncQueueState(repositories);

            if (refreshStorageMetrics)
            {
                await LoadLocalStorageMetricsAsync(silentMetrics, ct);
                await LoadCloudStorageMetricsAsync(active, silent: silentMetrics, ct);
            }

            UpdateSyncStatusAutoRefreshState();
        }
        finally
        {
            _syncStatusRefreshGate.Release();
        }
    }

    private async Task LoadCloudStorageMetricsAsync(
        UserProfileSessionDto? activeProfile,
        bool silent,
        CancellationToken ct = default)
    {
        if (!TryResolveCloudAccessToken(activeProfile, silent, out var accessToken))
            return;

        CloudStorageMetricsDto? metrics;
        try
        {
            metrics = await _cloudSyncService.GetStorageMetricsAsync(accessToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogInformation(
                ex,
                "Cloud storage metrics request requires re-authentication. BaseUrl {BaseUrl}",
                CloudApiBaseUrl);
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = LocalizeUserFacingMessage(ex.Message, "app_settings.cloud_storage_sign_in_required");
            return;
        }
        catch (Exception ex) when (IsCloudConnectivityFailure(ex))
        {
            _log.LogInformation(
                ex,
                "Cloud storage metrics endpoint is unavailable. BaseUrl {BaseUrl}",
                CloudApiBaseUrl);
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = Loc.F("app_settings.cloud_storage_metrics_offline", CloudApiBaseUrl);
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloud storage metrics request failed unexpectedly");
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = Loc.T("app_settings.cloud_storage_metrics_failed");
            return;
        }

        if (metrics is null)
        {
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = Loc.T("app_settings.cloud_storage_metrics_failed");
            return;
        }

        ApplyCloudStorageMetrics(metrics);
        if (!silent)
            CloudStorageMessage = Loc.T("app_settings.cloud_storage_metrics_loaded");
    }

    private async Task LoadLocalStorageMetricsAsync(bool silent, CancellationToken ct = default)
    {
        try
        {
            var metrics = await ExecuteIsolatedAsync<ILocalBlockStorageMetricsService, LocalBlockStorageMetricsDto>(
                (service, token) => service.GetMetricsAsync(token),
                ct);
            ApplyLocalStorageMetrics(metrics);
            if (!silent)
                LocalStorageMessage = Loc.T("app_settings.local_storage_metrics_loaded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local block storage metrics refresh failed");
            ClearLocalStorageMetrics();
            if (!silent)
                LocalStorageMessage = Loc.T("app_settings.local_storage_metrics_failed");
        }
    }

    private void ApplyCloudStorageMetrics(CloudStorageMetricsDto metrics)
    {
        _lastCloudStorageMetrics = metrics;
        HasCloudStorageMetrics = true;
        CloudStorageLogicalBlockCount = metrics.Summary.LogicalBlockCount;
        CloudStoragePhysicalObjectCount = metrics.Summary.PhysicalObjectCount;
        CloudStorageMissingBlockCount = metrics.Summary.MissingBlockCount;
        CloudStorageReductionText = $"{metrics.Summary.ReducedObjectPercentFloor}%";
        CloudStorageLogicalBytesText = FormatBytes(metrics.Summary.LogicalBytes);
        CloudStoragePhysicalPayloadBytesText = FormatBytes(metrics.Summary.PhysicalPayloadBytes);
        CloudStorageBlocksBreakdownText = Loc.F(
            "app_settings.cloud_storage_blocks_breakdown_format",
            metrics.Blocks.PackedBlocks,
            metrics.Blocks.LooseBlocks,
            metrics.Blocks.MissingBlocks);
        CloudStoragePacksBreakdownText = Loc.F(
            "app_settings.cloud_storage_packs_breakdown_format",
            metrics.Packs.TotalPacks,
            metrics.Packs.ActivePacks,
            metrics.Packs.SealedPacks);
        CloudStorageFilesystemBreakdownText = Loc.F(
            "app_settings.cloud_storage_filesystem_breakdown_format",
            metrics.Filesystem.PackFileCount,
            metrics.Filesystem.LooseFileCount,
            FormatBytes(metrics.Filesystem.TotalPhysicalBytes));
        CloudStorageLastUpdatedText = DateTime.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        UpdateSystemDiagnosticsCloudStorageState();
    }

    private void ApplyLocalStorageMetrics(LocalBlockStorageMetricsDto metrics)
    {
        _lastLocalStorageMetrics = metrics;
        HasLocalStorageMetrics = true;
        LocalStorageReferencedBlockCount = metrics.ReferencedBlockCount;
        LocalStorageUniqueBlockCount = metrics.UniqueBlockCount;
        LocalStorageMissingBlockCount = metrics.MissingBlockCount;
        LocalStorageReductionText = $"{metrics.ReducedPercentFloor}%";
        LocalStorageLogicalBytesText = FormatBytes(metrics.LogicalReferencedBytes);
        LocalStoragePhysicalBytesText = FormatBytes(metrics.PhysicalStoredBytes);
        LocalStorageFilesystemBreakdownText = Loc.F(
            "app_settings.local_storage_filesystem_breakdown_format",
            metrics.Filesystem.ManagedFileCount,
            metrics.Filesystem.NativeFileCount,
            metrics.Filesystem.OtherFileCount);
        LocalStorageRepositoryBreakdownText = Loc.F(
            "app_settings.local_storage_repository_breakdown_format",
            metrics.RepositoryCount,
            FormatBytes(metrics.SavedBytes));
        LocalStorageLastUpdatedText = DateTime.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        UpdateSystemDiagnosticsDedupState();
    }

    private bool RefreshRepositorySyncIssues(IReadOnlyList<RepositoryDto> repositories)
    {
        var hasActiveWork = false;
        var currentStallStates = new Dictionary<int, bool>();
        var nextItems = new List<AppRepositorySyncIssueItemViewModel>();

        foreach (var repository in repositories
                     .Where(HasSyncIssue)
                     .OrderByDescending(r => GetIssueSeverity(r.CloudSync))
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cloud = repository.CloudSync;
            var lastSyncText = cloud?.LastSyncedAtUtc is null
                ? Loc.T("common.never")
                : cloud.LastSyncedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var isStalled = IsSyncStalled(cloud);
            var stallText = FormatStallText(cloud);
            var issueCategoryCode = ResolveSyncIssueCategoryCode(cloud, isStalled);
            var issueCategoryText = LocalizeSyncIssueCategory(issueCategoryCode);
            var conflictStrategyText = LocalizeConflictStrategy(cloud?.ConflictStrategy);
            var recommendationText = BuildSyncIssueRecommendation(issueCategoryCode, cloud);

            currentStallStates[repository.Id] = isStalled;
            LogStallTransition(repository, isStalled, stallText);

            nextItems.Add(new AppRepositorySyncIssueItemViewModel(
                repository.Id,
                repository.Name,
                FormatCloudSyncStatus(cloud?.LastStatus),
                FormatCloudQueueSummary(
                    cloud?.PendingQueueCount ?? 0,
                    cloud?.RunningQueueCount ?? 0,
                    cloud?.RetryQueueCount ?? 0,
                    cloud?.ConflictQueueCount ?? 0,
                    cloud?.DeadLetterQueueCount ?? 0),
                lastSyncText,
                cloud is not null && cloud.UploadProgressTotal > 0,
                cloud?.UploadProgressCurrent ?? 0,
                cloud?.UploadProgressTotal ?? 0,
                CalculateProgressPercent(cloud?.UploadProgressCurrent ?? 0, cloud?.UploadProgressTotal ?? 0),
                FormatUploadProgress(cloud?.UploadProgressCurrent ?? 0, cloud?.UploadProgressTotal ?? 0),
                FormatEtaText(
                    cloud?.UploadProgressCurrent ?? 0,
                    cloud?.UploadProgressTotal ?? 0,
                    cloud?.UploadProgressStartedAtUtc,
                    cloud?.UploadProgressUpdatedAtUtc),
                FormatElapsedText(
                    cloud?.UploadProgressCurrent ?? 0,
                    cloud?.UploadProgressTotal ?? 0,
                    cloud?.UploadProgressStartedAtUtc,
                    cloud?.UploadProgressUpdatedAtUtc),
                FormatFinishAtText(
                    cloud?.UploadProgressCurrent ?? 0,
                    cloud?.UploadProgressTotal ?? 0,
                    cloud?.UploadProgressStartedAtUtc,
                    cloud?.UploadProgressUpdatedAtUtc),
                FormatLastProgressUpdateText(cloud?.UploadProgressUpdatedAtUtc),
                stallText,
                isStalled,
                cloud?.LastError ?? string.Empty,
                !string.IsNullOrWhiteSpace(cloud?.LastError),
                issueCategoryCode,
                issueCategoryText,
                conflictStrategyText,
                recommendationText,
                !string.IsNullOrWhiteSpace(recommendationText)));

            hasActiveWork |= HasActiveSyncWork(cloud);
        }

        _stallStateByRepositoryId.Clear();
        foreach (var pair in currentStallStates)
            _stallStateByRepositoryId[pair.Key] = pair.Value;

        ReplaceCollectionIfChanged(RepositorySyncIssues, nextItems);
        RepositorySyncIssueCount = nextItems.Count;
        return hasActiveWork;
    }

    private void UpdateSyncQueueState(IReadOnlyList<RepositoryDto> repositories)
    {
        var pending = 0;
        var running = 0;
        var retry = 0;
        var attention = 0;

        foreach (var repository in repositories)
        {
            var cloud = repository.CloudSync;
            if (cloud is null)
                continue;

            pending += Math.Max(0, cloud.PendingQueueCount);
            running += Math.Max(0, cloud.RunningQueueCount);
            retry += Math.Max(0, cloud.RetryQueueCount);
            attention += Math.Max(0, cloud.ConflictQueueCount)
                + Math.Max(0, cloud.FailedQueueCount)
                + Math.Max(0, cloud.DeadLetterQueueCount);
        }

        SyncQueuePendingCount = pending;
        SyncQueueRunningCount = running;
        SyncQueueRetryCount = retry;
        SyncQueueAttentionCount = attention;
    }

    private void LogStallTransition(RepositoryDto repository, bool isStalled, string stallText)
    {
        var hadPreviousState = _stallStateByRepositoryId.TryGetValue(repository.Id, out var previousState);
        if (hadPreviousState && previousState == isStalled)
            return;

        if (isStalled)
        {
            _log.LogWarning(
                "Repository sync appears stalled. RepositoryId {RepositoryId}. Name {RepositoryName}. LastStatus {LastStatus}. Progress {Current}/{Total}. LastProgressUpdateUtc {LastProgressUpdateUtc}. StallText {StallText}",
                repository.Id,
                repository.Name,
                repository.CloudSync?.LastStatus ?? "(none)",
                repository.CloudSync?.UploadProgressCurrent ?? 0,
                repository.CloudSync?.UploadProgressTotal ?? 0,
                repository.CloudSync?.UploadProgressUpdatedAtUtc,
                stallText);
        }
        else if (hadPreviousState && previousState)
        {
            _log.LogInformation(
                "Repository sync resumed after stall. RepositoryId {RepositoryId}. Name {RepositoryName}. LastStatus {LastStatus}. Progress {Current}/{Total}. LastProgressUpdateUtc {LastProgressUpdateUtc}",
                repository.Id,
                repository.Name,
                repository.CloudSync?.LastStatus ?? "(none)",
                repository.CloudSync?.UploadProgressCurrent ?? 0,
                repository.CloudSync?.UploadProgressTotal ?? 0,
                repository.CloudSync?.UploadProgressUpdatedAtUtc);
        }
    }

    private static bool HasSyncIssue(RepositoryDto repository)
    {
        var cloud = repository.CloudSync;
        if (cloud is null)
            return false;

        if (!string.IsNullOrWhiteSpace(cloud.LastError))
            return true;

        if (cloud.PendingQueueCount > 0
            || cloud.RunningQueueCount > 0
            || cloud.RetryQueueCount > 0
            || cloud.ConflictQueueCount > 0
            || cloud.DeadLetterQueueCount > 0
            || cloud.FailedQueueCount > 0)
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
                   or "retrying"
                   or "auth_required"
                   or "conflict"
                   or "failed"
                   or "dead_letter";
    }

    private static bool HasActiveSyncWork(RepositoryCloudSyncStatusDto? cloud)
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

    private static bool IsSyncStalled(RepositoryCloudSyncStatusDto? cloud)
    {
        if (cloud is null)
            return false;

        if (cloud.UploadProgressTotal <= 0 || cloud.UploadProgressCurrent >= cloud.UploadProgressTotal)
            return false;

        var normalizedStatus = cloud.LastStatus?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedStatus))
            return false;

        if (!normalizedStatus.StartsWith("syncing_upload", StringComparison.Ordinal)
            && normalizedStatus is not ("syncing" or "syncing_prepare" or "syncing_snapshot" or "syncing_finalize" or "retrying" or "offline_retry"))
        {
            return false;
        }

        if (cloud.UploadProgressUpdatedAtUtc is null)
            return false;

        return DateTime.UtcNow - cloud.UploadProgressUpdatedAtUtc.Value >= SyncProgressStallThreshold;
    }

    private void UpdateSyncStatusAutoRefreshState()
    {
        var shouldRun = IsSyncTabSelected && _hasActiveSyncWork;
        if (shouldRun)
        {
            if (_syncStatusAutoRefreshCts is not null)
                return;

            _syncStatusAutoRefreshCts = new CancellationTokenSource();
            var token = _syncStatusAutoRefreshCts.Token;
            _syncStatusAutoRefreshTask = Task.Run(() => RunSyncStatusAutoRefreshAsync(token), token);
            _log.LogInformation(
                "Sync status auto-refresh started. IntervalSeconds {IntervalSeconds}",
                SyncStatusAutoRefreshInterval.TotalSeconds);
            return;
        }

        if (_syncStatusAutoRefreshCts is null)
            return;

        _syncStatusAutoRefreshCts.Cancel();
        _syncStatusAutoRefreshCts.Dispose();
        _syncStatusAutoRefreshCts = null;
        _syncStatusAutoRefreshTask = null;
        _log.LogInformation("Sync status auto-refresh stopped");
    }

    private async Task RunSyncStatusAutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SyncStatusAutoRefreshInterval, ct);
                await Dispatcher.UIThread.InvokeAsync(
                    () => RefreshSyncSectionAsync(silentMetrics: true, refreshStorageMetrics: false, ct),
                    DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync status auto-refresh loop failed");
        }
    }

    private static int GetIssueSeverity(RepositoryCloudSyncStatusDto? cloud)
    {
        if (cloud is null)
            return 0;

        if (cloud.DeadLetterQueueCount > 0 || cloud.FailedQueueCount > 0)
            return 5;
        if (cloud.ConflictQueueCount > 0)
            return 4;
        if (!string.IsNullOrWhiteSpace(cloud.LastError))
            return 3;
        if (cloud.RetryQueueCount > 0)
            return 2;
        if (cloud.PendingQueueCount > 0 || cloud.RunningQueueCount > 0)
            return 1;
        return 0;
    }

    private static string ResolveSyncIssueCategoryCode(RepositoryCloudSyncStatusDto? cloud, bool isStalled)
    {
        if (cloud is null)
            return "attention";

        var normalizedStatus = cloud.LastStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedError = cloud.LastError?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalizedStatus == "auth_required"
            || normalizedError.Contains("unauthorized", StringComparison.Ordinal)
            || normalizedError.Contains("token", StringComparison.Ordinal))
        {
            return "auth";
        }

        if (cloud.ConflictQueueCount > 0 || normalizedStatus == "conflict")
            return "conflict";

        if (isStalled)
            return "stalled";

        if (cloud.DeadLetterQueueCount > 0 || cloud.FailedQueueCount > 0 || normalizedStatus is "failed" or "dead_letter")
            return "failed";

        if (cloud.RetryQueueCount > 0 || normalizedStatus is "retrying" or "offline_retry")
            return "retry";

        if (cloud.PendingQueueCount > 0
            || cloud.RunningQueueCount > 0
            || normalizedStatus.StartsWith("syncing_upload", StringComparison.Ordinal)
            || normalizedStatus is "queued" or "syncing" or "syncing_prepare" or "syncing_snapshot" or "syncing_finalize")
            return "queue";

        return "attention";
    }

    private static string LocalizeSyncIssueCategory(string code)
    {
        return code switch
        {
            "auth" => Loc.T("app_settings.sync_issue_category_auth"),
            "conflict" => Loc.T("app_settings.sync_issue_category_conflict"),
            "stalled" => Loc.T("app_settings.sync_issue_category_stalled"),
            "retry" => Loc.T("app_settings.sync_issue_category_retry"),
            "failed" => Loc.T("app_settings.sync_issue_category_failed"),
            "queue" => Loc.T("app_settings.sync_issue_category_queue"),
            _ => Loc.T("app_settings.sync_issue_category_attention")
        };
    }

    private static string LocalizeConflictStrategy(string? strategy)
    {
        return RepositorySyncConflictStrategies.Normalize(strategy) switch
        {
            RepositorySyncConflictStrategies.ManualMerge => Loc.T("app_settings.sync_conflict_strategy_manual_merge"),
            RepositorySyncConflictStrategies.PreserveBoth => Loc.T("app_settings.sync_conflict_strategy_preserve_both"),
            _ => Loc.T("app_settings.sync_conflict_strategy_last_write_wins")
        };
    }

    private static string BuildSyncIssueRecommendation(string categoryCode, RepositoryCloudSyncStatusDto? cloud)
    {
        return categoryCode switch
        {
            "auth" => Loc.T("app_settings.sync_issue_recommendation_auth"),
            "conflict" when RepositorySyncConflictStrategies.Normalize(cloud?.ConflictStrategy) == RepositorySyncConflictStrategies.ManualMerge
                => Loc.T("app_settings.sync_issue_recommendation_conflict_manual"),
            "conflict" when RepositorySyncConflictStrategies.Normalize(cloud?.ConflictStrategy) == RepositorySyncConflictStrategies.PreserveBoth
                => Loc.T("app_settings.sync_issue_recommendation_conflict_preserve"),
            "conflict" => Loc.T("app_settings.sync_issue_recommendation_conflict_overwrite"),
            "stalled" => Loc.T("app_settings.sync_issue_recommendation_stalled"),
            "retry" => Loc.T("app_settings.sync_issue_recommendation_retry"),
            "failed" => Loc.T("app_settings.sync_issue_recommendation_failed"),
            "queue" => Loc.T("app_settings.sync_issue_recommendation_queue"),
            _ => Loc.T("app_settings.sync_issue_recommendation_attention")
        };
    }

    private static string FormatCloudSyncStatus(string? status)
    {
        if (UserExperienceManager.Instance.IsBasicMode)
        {
            if (string.IsNullOrWhiteSpace(status))
                return Loc.T("repo_settings.sync_status_basic_local");

            var normalizedBasic = status.Trim().ToLowerInvariant();
            return normalizedBasic switch
            {
                _ when normalizedBasic.StartsWith("syncing_upload", StringComparison.Ordinal) => Loc.T("repo_settings.sync_status_basic_working"),
                "queued" or "syncing" or "syncing_prepare" or "syncing_snapshot" or "syncing_finalize" or "offline_retry" or "retrying"
                    => Loc.T("repo_settings.sync_status_basic_working"),
                "auth_required" or "conflict" or "failed" or "dead_letter" or "paused" or "cancelled" => Loc.T("repo_settings.sync_status_basic_attention"),
                "linked" => Loc.T("repo_settings.sync_status_basic_ready"),
                _ when normalizedBasic.StartsWith("synced", StringComparison.Ordinal) => Loc.T("repo_settings.sync_status_basic_ready"),
                _ => Loc.T("repo_settings.sync_status_basic_local")
            };
        }

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

    private static string FormatCloudQueueSummary(int pending, int running, int retry, int conflict, int deadLetter)
    {
        if (pending == 0 && running == 0 && retry == 0 && conflict == 0 && deadLetter == 0)
            return Loc.T("repo_settings.queue_basic_idle");

        if (conflict > 0 || deadLetter > 0)
            return Loc.T("repo_settings.sync_status_basic_attention");

        return Loc.T("repo_settings.queue_basic_active");
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

        var remainingItems = total - current;
        var remainingSeconds = remainingItems / rate;
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

    private static string FormatFinishAtText(int current, int total, DateTime? startedAtUtc, DateTime? updatedAtUtc)
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

        var finishAtLocal = referenceUtc.AddSeconds(remainingSeconds).ToLocalTime();
        return Loc.F("app_settings.repository_sync_health_finish_at_format", finishAtLocal.ToString("HH:mm:ss"));
    }

    private static string FormatLastProgressUpdateText(DateTime? updatedAtUtc)
    {
        if (updatedAtUtc is null)
            return Loc.T("common.not_available_short");

        return Loc.F(
            "app_settings.repository_sync_health_last_update_format",
            updatedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss"));
    }

    private static string FormatStallText(RepositoryCloudSyncStatusDto? cloud)
    {
        if (!IsSyncStalled(cloud) || cloud?.UploadProgressUpdatedAtUtc is null)
            return string.Empty;

        var stalledFor = DateTime.UtcNow - cloud.UploadProgressUpdatedAtUtc.Value;
        if (stalledFor < TimeSpan.Zero)
            stalledFor = TimeSpan.Zero;

        return Loc.F(
            "app_settings.repository_sync_health_stalled_format",
            FormatDuration(stalledFor));
    }

    private void NotifyMonitoringStateChanged()
    {
        OnPropertyChanged(nameof(MonitoringStateText));
        OnPropertyChanged(nameof(MonitoringHelpText));
        OnPropertyChanged(nameof(ShowMonitoringProcessLoad));
        OnPropertyChanged(nameof(HasMonitoringProcessHistory));
        OnPropertyChanged(nameof(MonitoringProcessLoadSummaryText));
        OnPropertyChanged(nameof(MonitoringProcessLoadDetailText));
        OnPropertyChanged(nameof(MonitoringProcessLoadPeakText));
        OnPropertyChanged(nameof(MonitoringProcessLoadHistoryText));
        OnPropertyChanged(nameof(MonitoringProcessLoadAccentColor));
        OnPropertyChanged(nameof(MonitoringProcessLoadBackgroundColor));
    }

    private void ClearCloudStorageMetrics()
    {
        _lastCloudStorageMetrics = null;
        HasCloudStorageMetrics = false;
        CloudStorageLogicalBlockCount = 0;
        CloudStoragePhysicalObjectCount = 0;
        CloudStorageMissingBlockCount = 0;
        CloudStorageReductionText = Loc.T("common.not_available_short");
        CloudStorageLogicalBytesText = Loc.T("common.not_available_short");
        CloudStoragePhysicalPayloadBytesText = Loc.T("common.not_available_short");
        CloudStorageBlocksBreakdownText = Loc.T("common.not_available_short");
        CloudStoragePacksBreakdownText = Loc.T("common.not_available_short");
        CloudStorageFilesystemBreakdownText = Loc.T("common.not_available_short");
        CloudStorageLastUpdatedText = Loc.T("common.not_available_short");
        UpdateSystemDiagnosticsCloudStorageState();
    }

    private void ClearLocalStorageMetrics()
    {
        _lastLocalStorageMetrics = null;
        HasLocalStorageMetrics = false;
        LocalStorageReferencedBlockCount = 0;
        LocalStorageUniqueBlockCount = 0;
        LocalStorageMissingBlockCount = 0;
        LocalStorageReductionText = Loc.T("common.not_available_short");
        LocalStorageLogicalBytesText = Loc.T("common.not_available_short");
        LocalStoragePhysicalBytesText = Loc.T("common.not_available_short");
        LocalStorageFilesystemBreakdownText = Loc.T("common.not_available_short");
        LocalStorageRepositoryBreakdownText = Loc.T("common.not_available_short");
        LocalStorageLastUpdatedText = Loc.T("common.not_available_short");
        UpdateSystemDiagnosticsDedupState();
    }

    private void ClearSystemDiagnostics()
    {
        _lastDiagnosticsReport = null;
        HasSystemDiagnostics = false;
        SystemDiagnosticsLastUpdatedText = Loc.T("common.not_available_short");
        SystemDiagnosticsTargetRepositoryText = Loc.T("common.not_available_short");
        SystemDiagnosticsMemoryText = Loc.T("common.not_available_short");
        SystemDiagnosticsMemoryStatusText = Loc.T("app_settings.system_diagnostics_memory_unavailable");
        SystemDiagnosticsCloudUsageText = Loc.T("common.not_available_short");
        SystemDiagnosticsCloudUsageStatusText = Loc.T("app_settings.system_diagnostics_cloud_usage_unavailable");
        SystemDiagnosticsHistoryText = Loc.T("common.not_available_short");
        SystemDiagnosticsHistoryStatusText = Loc.T("app_settings.system_diagnostics_history_unavailable");
        SystemDiagnosticsScanText = Loc.T("common.not_available_short");
        SystemDiagnosticsScanStatusText = Loc.T("app_settings.system_diagnostics_scan_unavailable");
        SystemDiagnosticsNativeRuntimeText = Loc.T("app_settings.system_diagnostics_native_unavailable");
        UpdateSystemDiagnosticsDedupState();
    }

    private void ApplySystemDiagnostics(AppDiagnosticsReportDto report)
    {
        _lastDiagnosticsReport = report;
        HasSystemDiagnostics = true;
        SystemDiagnosticsLastUpdatedText = report.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        SystemDiagnosticsTargetRepositoryText = report.HasTargetRepository
            ? Loc.F(
                "app_settings.system_diagnostics_target_repository_format",
                report.TargetRepositoryName ?? Loc.T("common.not_available_short"),
                report.TargetRepositoryPath ?? Loc.T("common.not_available_short"))
            : Loc.T("app_settings.system_diagnostics_no_repository");

        var process = report.Process;
        SystemDiagnosticsMemoryText = FormatBytes(process.WorkingSetBytes);
        SystemDiagnosticsMemoryStatusText = process.MeetsRecommendedLimit
            ? Loc.F(
                "app_settings.system_diagnostics_memory_ok",
                FormatBytes(process.RecommendedLimitBytes),
                FormatBytes(process.PrivateMemoryBytes),
                FormatBytes(process.ManagedHeapBytes))
            : Loc.F(
                "app_settings.system_diagnostics_memory_warning",
                FormatBytes(process.RecommendedLimitBytes),
                FormatBytes(process.PrivateMemoryBytes),
                FormatBytes(process.ManagedHeapBytes));

        var history = report.History;
        if (history.Available)
        {
            SystemDiagnosticsHistoryText = $"{history.LatestEntriesLoadMs} ms";
            SystemDiagnosticsHistoryStatusText = history.MeetsLatestEntriesTarget
                ? Loc.F(
                    "app_settings.system_diagnostics_history_ok",
                    history.LatestEntriesLoadMs,
                    history.SnapshotHistoryLoadMs,
                    history.LatestEntriesCount)
                : Loc.F(
                    "app_settings.system_diagnostics_history_warning",
                    history.LatestEntriesLoadMs,
                    history.TargetMs,
                    history.SnapshotHistoryLoadMs);
        }
        else
        {
            SystemDiagnosticsHistoryText = Loc.T("common.not_available_short");
            SystemDiagnosticsHistoryStatusText = Loc.F(
                "app_settings.system_diagnostics_history_unavailable_with_reason",
                history.ErrorMessage ?? Loc.T("common.not_available_short"));
        }

        var scan = report.Scan;
        if (scan.Available)
        {
            if (scan.FileCount <= 0 || scan.TotalFileBytes <= 0 || scan.ThroughputMbPerSecond <= 0)
            {
                SystemDiagnosticsScanText = Loc.T("common.not_available_short");
                SystemDiagnosticsScanStatusText = Loc.T("app_settings.system_diagnostics_scan_no_matching_files");
            }
            else
            {
                SystemDiagnosticsScanText = FormatThroughput(scan.ThroughputMbPerSecond);
                SystemDiagnosticsScanStatusText = scan.MeetsRecommendedTarget
                    ? Loc.F(
                        "app_settings.system_diagnostics_scan_ok",
                        FormatThroughput(scan.ThroughputMbPerSecond),
                        scan.FileCount,
                        FormatBytes(scan.TotalFileBytes))
                    : Loc.F(
                        "app_settings.system_diagnostics_scan_warning",
                        FormatThroughput(scan.ThroughputMbPerSecond),
                        $"{scan.TargetMbPerSecond:0.#}");
            }
        }
        else
        {
            SystemDiagnosticsScanText = Loc.T("common.not_available_short");
            SystemDiagnosticsScanStatusText = Loc.F(
                "app_settings.system_diagnostics_scan_unavailable_with_reason",
                scan.ErrorMessage ?? Loc.T("common.not_available_short"));
        }

        var nativeRuntime = report.NativeRuntime;
        SystemDiagnosticsNativeRuntimeText = BuildNativeRuntimeSummary(nativeRuntime);

        UpdateSystemDiagnosticsCloudStorageState();
        UpdateSystemDiagnosticsDedupState();
    }

    private void UpdateSystemDiagnosticsCloudStorageState()
    {
        if (_lastCloudStorageMetrics is null)
        {
            SystemDiagnosticsCloudUsageText = Loc.T("common.not_available_short");
            SystemDiagnosticsCloudUsageStatusText = Loc.T("app_settings.system_diagnostics_cloud_usage_unavailable");
            return;
        }

        var logicalBytes = Math.Max(0L, _lastCloudStorageMetrics.Summary.LogicalBytes);
        var physicalBytes = Math.Max(0L, _lastCloudStorageMetrics.Summary.PhysicalPayloadBytes);
        SystemDiagnosticsCloudUsageText = FormatBytes(physicalBytes);

        if (logicalBytes <= 0)
        {
            SystemDiagnosticsCloudUsageStatusText = Loc.T("app_settings.system_diagnostics_cloud_usage_unavailable");
            return;
        }

        if (physicalBytes >= logicalBytes)
        {
            SystemDiagnosticsCloudUsageStatusText = Loc.T("app_settings.system_diagnostics_cloud_usage_no_reduction");
            return;
        }

        var savedBytes = logicalBytes - physicalBytes;
        var reducedPercent = Math.Clamp(savedBytes * 100d / logicalBytes, 0d, 100d);
        if (reducedPercent < 3d)
        {
            SystemDiagnosticsCloudUsageStatusText = Loc.T("app_settings.system_diagnostics_cloud_usage_no_reduction");
            return;
        }

        SystemDiagnosticsCloudUsageStatusText = Loc.F(
            "app_settings.system_diagnostics_cloud_usage_reduced",
            Math.Round(reducedPercent),
            FormatBytes(savedBytes));
    }

    private void UpdateSystemDiagnosticsDedupState()
    {
        if (_lastLocalStorageMetrics is null)
        {
            SystemDiagnosticsDedupText = Loc.T("common.not_available_short");
            SystemDiagnosticsDedupStatusText = Loc.T("app_settings.system_diagnostics_dedup_unavailable");
            return;
        }

        SystemDiagnosticsDedupText = $"{_lastLocalStorageMetrics.ReducedPercentFloor}%";
        SystemDiagnosticsDedupStatusText = _lastLocalStorageMetrics.ReducedPercentFloor >= 40
            ? Loc.F("app_settings.system_diagnostics_dedup_ok", _lastLocalStorageMetrics.ReducedPercentFloor)
            : Loc.F("app_settings.system_diagnostics_dedup_warning", _lastLocalStorageMetrics.ReducedPercentFloor, 40);
    }

    private async Task SaveSensitiveActionVerificationSettingAsync(bool value)
    {
        if (!HasActiveProfile)
        {
            GeneralMessage = Loc.T("app_settings.sensitive_actions_requires_sign_in");
            return;
        }

        try
        {
            var updated = await _userProfiles.SetRequirePasswordForSensitiveActionsAsync(value);
            if (!updated)
            {
                GeneralMessage = Loc.T("app_settings.sensitive_actions_save_failed");
                return;
            }

            GeneralMessage = value
                ? Loc.T("app_settings.sensitive_actions_enabled")
                : Loc.T("app_settings.sensitive_actions_disabled");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update sensitive action verification setting");
            GeneralMessage = Loc.T("app_settings.sensitive_actions_save_failed");
        }
    }

    private void UpdateLocalizationDiagnostics()
    {
        var diagnostics = _localization.CurrentDiagnostics;

        LocalizationResourceFilesCount = diagnostics.ResourceFileCount;
        LocalizationTotalKeysCount = diagnostics.TotalKeysCount;
        LocalizationMissingKeysCount = diagnostics.MissingKeysCount;
        LocalizationDuplicateKeysCount = diagnostics.DuplicateKeysCount;
        LocalizationExtraKeysCount = diagnostics.ExtraKeysCount;

        LocalizationMissingSample = diagnostics.MissingKeysSample.Count > 0
            ? string.Join(", ", diagnostics.MissingKeysSample)
            : string.Empty;

        LocalizationDuplicateSample = diagnostics.DuplicateKeysSample.Count > 0
            ? string.Join(", ", diagnostics.DuplicateKeysSample)
            : string.Empty;

        LocalizationExtraSample = diagnostics.ExtraKeysSample.Count > 0
            ? string.Join(", ", diagnostics.ExtraKeysSample)
            : string.Empty;

        LocalizationHealthText = HasLocalizationIssues
            ? Loc.T("app_settings.localization_health_warning")
            : Loc.T("app_settings.localization_health_ok");
    }
    private void RebuildLanguageOptions()
    {
        _suppressLanguageSelectionChanged = true;
        try
        {
            Languages.Clear();
            foreach (var language in _localization.AvailableLanguages)
            {
                Languages.Add(new AppLanguageOptionItemViewModel(language.Code, language.DisplayName));
            }

            SyncSelectedLanguageOption(rebuildIfMissing: false);
        }
        finally
        {
            _suppressLanguageSelectionChanged = false;
        }
    }

    private void SyncSelectedLanguageOption(bool rebuildIfMissing)
    {
        var match = Languages.FirstOrDefault(x =>
            string.Equals(x.Code, _localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            if (!EqualityComparer<AppLanguageOptionItemViewModel?>.Default.Equals(SelectedLanguage, match))
                SelectedLanguage = match;

            return;
        }

        if (rebuildIfMissing)
        {
            RebuildLanguageOptions();
            return;
        }

        if (SelectedLanguage is not null)
            SelectedLanguage = null;
    }

    private void RebuildThemeOptions()
    {
        _suppressThemeSelectionChanged = true;
        try
        {
            Themes.Clear();
            foreach (var option in _theme.AvailableThemes)
            {
                Themes.Add(new AppThemeOptionItemViewModel(
                    option.Code,
                    Loc.T(option.LocalizationKey)));
            }

            SyncSelectedThemeOption(rebuildIfMissing: false);
        }
        finally
        {
            _suppressThemeSelectionChanged = false;
        }
    }

    private void RebuildExperienceOptions()
    {
        _suppressExperienceSelectionChanged = true;
        try
        {
            ExperienceModes.Clear();
            foreach (var option in _experience.AvailableModes)
            {
                ExperienceModes.Add(new AppExperienceOptionItemViewModel(
                    option.Code,
                    Loc.T(option.LocalizationKey),
                    Loc.T(option.DescriptionKey)));
            }

            SelectedExperienceMode = ExperienceModes.FirstOrDefault(x =>
                string.Equals(x.Code, _experience.CurrentModeCode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressExperienceSelectionChanged = false;
        }
    }

    private void SyncSelectedThemeOption(bool rebuildIfMissing)
    {
        var match = Themes.FirstOrDefault(x =>
            string.Equals(x.Code, _theme.CurrentThemeCode, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            if (!EqualityComparer<AppThemeOptionItemViewModel?>.Default.Equals(SelectedTheme, match))
                SelectedTheme = match;

            return;
        }

        if (rebuildIfMissing)
            RebuildThemeOptions();
    }

    public bool ShowConnectivityBanner
        => HasActiveProfile
           && IsSyncTabSelected
           && (_connectivity.Snapshot.State is ConnectivityState.InternetUnavailable or ConnectivityState.CloudUnavailable
               || _cloudSyncRuntime.IsPaused);

    public bool CanSyncToCloud
        => HasActiveProfile
           && !IsSyncBusy
           && _connectivity.Snapshot.State == ConnectivityState.Online;

    public string ConnectivityBannerAccentColor
        => _cloudSyncRuntime.IsPaused && HasActiveProfile
            ? "#6EA8FF"
            : _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => "#F59E0B",
            ConnectivityState.CloudUnavailable => "#F59E0B",
            _ => "#6EA8FF"
        };

    public string ConnectivityBannerBackgroundColor
        => _cloudSyncRuntime.IsPaused && HasActiveProfile
            ? "#1A6EA8FF"
            : _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => "#1AF59E0B",
            ConnectivityState.CloudUnavailable => "#1AF59E0B",
            _ => "#1A6EA8FF"
        };

    public string ConnectivityBannerText
        => _cloudSyncRuntime.IsPaused && HasActiveProfile
            ? Loc.T("ui_error.cloud_actions_paused")
            : _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("connectivity.banner.internet_required"),
            ConnectivityState.CloudUnavailable => Loc.T("connectivity.banner.cloud_unavailable"),
            _ => string.Empty
        };

    private (long RequestId, CancellationToken Token) BeginLoadRequest()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var requestId = Interlocked.Increment(ref _loadRequestId);
        return (requestId, cts.Token);
    }

    private bool IsLatestLoadRequest(long requestId)
        => requestId == Interlocked.Read(ref _loadRequestId);

    private Task<TResponse> SendIsolatedAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        => _scopeExecutor.ExecuteAsync<IMediator, TResponse>((mediator, token) => mediator.Send(request, token), ct);

    private Task<TResult> ExecuteIsolatedAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
        where TService : notnull
        => _scopeExecutor.ExecuteAsync(operation, ct);

    private async Task RunDeferredSettingsLoadAsync(
        long requestId,
        UserProfileSessionDto? activeProfile,
        CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                RunDeferredSettingsSectionAsync("windows_autostart", () => LoadWindowsAutostartStateAsync(ct), ct),
                RunDeferredSettingsSectionAsync("artifact_encryption", () => LoadArtifactEncryptionStateAsync(ct), ct),
                RunDeferredSettingsSectionAsync("local_storage_metrics", () => LoadLocalStorageMetricsAsync(silent: true, ct), ct),
                RunDeferredSettingsSectionAsync("cloud_storage_metrics", () => LoadCloudStorageMetricsAsync(activeProfile, silent: true, ct), ct),
                RunDeferredSettingsSectionAsync("operation_journal", () => LoadOperationJournalAsync(ct), ct));

            if (!IsLatestLoadRequest(requestId) || ct.IsCancellationRequested)
                return;

            _log.LogInformation("App settings deferred sections loaded");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task RunDeferredSettingsSectionAsync(
        string sectionName,
        Func<Task> loadAsync,
        CancellationToken ct)
    {
        try
        {
            await loadAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Deferred app settings section load failed. Section {Section}", sectionName);
        }
    }

    private async Task LoadOperationJournalAsync(CancellationToken ct = default)
    {
        try
        {
            var entries = await ExecuteIsolatedAsync<IOperationJournalService, IReadOnlyList<OperationJournalEntryDto>>(
                (journal, token) => journal.GetRecentAsync(5, token),
                ct);
            OperationJournalItems.Clear();

            foreach (var entry in entries)
            {
                var scope = entry.RepositoryId.HasValue
                    ? Loc.F("operation_journal.scope_repository", entry.RepositoryId.Value)
                    : string.IsNullOrWhiteSpace(entry.Username)
                        ? Loc.T("common.not_available_short")
                        : entry.Username!;

                OperationJournalItems.Add(new AppOperationJournalItemViewModel(
                    entry.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    HumanizeJournalValue(entry.Level),
                    HumanizeJournalValue(entry.Category),
                    HumanizeJournalAction(entry.Action),
                    scope,
                    LocalizeUserFacingMessage(entry.Message, "common.not_available_short")));
            }

            OnPropertyChanged(nameof(HasOperationJournalItems));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load operation journal");
            OperationJournalItems.Clear();
            OnPropertyChanged(nameof(HasOperationJournalItems));
        }
    }

    private async Task AppendJournalAsync(
        string level,
        string category,
        string action,
        string message,
        string? username)
    {
        try
        {
            await _journal.AppendAsync(new OperationJournalEntryDto(
                Id: 0,
                OccurredAtUtc: DateTime.UtcNow,
                Level: level,
                Category: category,
                Action: action,
                RepositoryId: null,
                Username: string.IsNullOrWhiteSpace(username) ? null : username,
                Message: message,
                Details: null));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Skipped appending operation journal entry for action {Action}", action);
        }
    }

    private async Task OpenAuthDialogAsync(bool registerMode)
    {
        try
        {
            var owner = _windows.GetActiveWindow();
            var window = _windows.Create<AuthDialogWindow>();
            if (window.DataContext is not AuthDialogWindowViewModel vm)
                return;

            vm.Configure(registerMode);

            if (owner is not null)
                await _windows.ShowDialogAsync(window, owner);
            else
                _windows.Show(window);

            if (!vm.IsSuccessful)
                return;

            AuthMessage = vm.ResultMessage;
            await AppendJournalAsync(
                "info",
                "auth",
                registerMode ? "dialog_register" : "dialog_login",
                vm.ResultMessage,
                ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open auth dialog. RegisterMode {RegisterMode}", registerMode);
            AuthMessage = Loc.T("app_settings.auth_request_failed");
        }
    }

    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var trimmed = value.Trim();
            var parsed = new MailAddress(trimmed);
            return string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string HumanizeJournalAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Loc.T("common.not_available_short");

        return value.Trim() switch
        {
            "ScanRepositoryCommand" => Loc.T("operation_journal.action.scan_repository"),
            "EnsureRepositoriesCommand" => Loc.T("operation_journal.action.refresh_repositories"),
            "CreateRepositoryWithFormatsCommand" or "CreateRepositoryCommand" or "AddDirectoryAndCreateRepositoryCommand" => Loc.T("operation_journal.action.create_repository"),
            "dialog_login" => Loc.T("operation_journal.action.sign_in"),
            "dialog_register" => Loc.T("operation_journal.action.register"),
            "settings_cloud_storage_refresh" => Loc.T("operation_journal.action.refresh_cloud_status"),
            "settings_cloud_storage_repair" => Loc.T("operation_journal.action.repair_cloud_storage"),
            "scheduled_integrity_verification" => Loc.T("operation_journal.action.scheduled_integrity_verification"),
            _ => HumanizeJournalValue(value)
        };
    }

    private static string HumanizeJournalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Loc.T("common.not_available_short");

        var normalized = value.Trim().Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));
    }

    private static void ReplaceCollectionIfChanged<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        if (CollectionEquals(collection, items))
            return;

        SyncCollection(collection, items);
    }

    private static bool CollectionEquals<T>(IReadOnlyList<T> current, IReadOnlyList<T> next)
    {
        if (current.Count != next.Count)
            return false;

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < current.Count; i++)
        {
            if (!comparer.Equals(current[i], next[i]))
                return false;
        }

        return true;
    }

    private static void SyncCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        var comparer = EqualityComparer<T>.Default;
        var sharedPrefix = 0;
        var maxPrefix = Math.Min(collection.Count, items.Count);
        while (sharedPrefix < maxPrefix && comparer.Equals(collection[sharedPrefix], items[sharedPrefix]))
            sharedPrefix++;

        var sharedSuffix = 0;
        var maxSuffix = Math.Min(collection.Count - sharedPrefix, items.Count - sharedPrefix);
        while (sharedSuffix < maxSuffix &&
               comparer.Equals(
                   collection[collection.Count - 1 - sharedSuffix],
                   items[items.Count - 1 - sharedSuffix]))
        {
            sharedSuffix++;
        }

        var removeStart = sharedPrefix;
        var removeCount = collection.Count - sharedPrefix - sharedSuffix;
        for (var i = 0; i < removeCount; i++)
            collection.RemoveAt(removeStart);

        var insertCount = items.Count - sharedPrefix - sharedSuffix;
        for (var i = 0; i < insertCount; i++)
            collection.Insert(removeStart + i, items[sharedPrefix + i]);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }

    private static string FormatThroughput(double megabytesPerSecond)
    {
        if (double.IsNaN(megabytesPerSecond) || double.IsInfinity(megabytesPerSecond) || megabytesPerSecond <= 0)
            return Loc.T("common.not_available_short");

        return $"{megabytesPerSecond:0.#} MB/s";
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

    private static string FormatHourRange(int hour)
        => $"{Math.Clamp(hour, 0, 23):00}:00";

    private static int? ParseNullablePositiveInt(string? value, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : null;
    }

    private static long? ParseNullablePositiveLong(string? value, long min, long max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : null;
    }

    private bool TryResolveCloudAccessToken(
        UserProfileSessionDto? activeProfile,
        bool silent,
        out string accessToken)
    {
        accessToken = string.Empty;

        var evaluation = _tokenPolicy.Evaluate(activeProfile?.AccessToken);
        if (!evaluation.CanUseForSync || string.IsNullOrWhiteSpace(activeProfile?.AccessToken))
        {
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = LocalizeUserFacingMessage(evaluation.Description, "app_settings.cloud_storage_sign_in_required");

            return false;
        }

        accessToken = activeProfile.AccessToken!;
        return true;
    }

    private static string LocalizeUserFacingMessage(string? message, string fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Loc.T(fallbackKey);

        return UserFacingMessageLocalizer.TryLocalize(message) ?? message.Trim();
    }

    private static string LocalizeArtifactKeyStatus(string status)
    {
        return ArtifactKeyStatus.Normalize(status) switch
        {
            ArtifactKeyStatus.Active => Loc.T("app_settings.artifact_key_status_active"),
            ArtifactKeyStatus.Retired => Loc.T("app_settings.artifact_key_status_retired"),
            ArtifactKeyStatus.Revoked => Loc.T("app_settings.artifact_key_status_revoked"),
            _ => status
        };
    }

    private static bool IsCloudConnectivityFailure(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
            return true;

        return ex.InnerException is not null && IsCloudConnectivityFailure(ex.InnerException);
    }

    private string? ResolveCloudConnectivityMessage()
        => !HasActiveProfile
            ? Loc.T("app_settings.guest_auth_hint")
            : _cloudSyncRuntime.IsPaused
            ? Loc.T("ui_error.cloud_actions_paused")
            : _connectivity.Snapshot.State switch
        {
            ConnectivityState.InternetUnavailable => Loc.T("ui_error.internet_required"),
            ConnectivityState.CloudUnavailable => Loc.T("ui_error.cloud_temporarily_unavailable"),
            _ => null
        };

    private async Task ExportSystemDiagnosticsAsync(bool asJson)
    {
        if (_lastDiagnosticsReport is null)
        {
            SystemDiagnosticsMessage = Loc.T("app_settings.system_diagnostics_export_no_data");
            return;
        }

        var owner = _windows.GetActiveWindow();
        if (owner is null)
        {
            SystemDiagnosticsMessage = Loc.T("app_settings.system_diagnostics_export_picker_unavailable");
            return;
        }

        try
        {
            var extension = asJson ? "json" : "txt";
            var suggestedName = BuildDiagnosticsSuggestedFileName(extension);
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T(asJson
                    ? "app_settings.system_diagnostics_export_json_title"
                    : "app_settings.system_diagnostics_export_text_title"),
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(Loc.T(asJson
                        ? "app_settings.system_diagnostics_export_json_button"
                        : "app_settings.system_diagnostics_export_text_button"))
                    {
                        Patterns = [asJson ? "*.json" : "*.txt"]
                    }
                ]
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            if (asJson)
            {
                var payload = BuildDiagnosticsExportPayload(_lastDiagnosticsReport, _lastLocalStorageMetrics);
                await JsonSerializer.SerializeAsync(stream, payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await stream.FlushAsync();
            }
            else
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(BuildDiagnosticsTextReport(_lastDiagnosticsReport, _lastLocalStorageMetrics));
                await writer.FlushAsync();
            }

            var savedPath = StoragePathResolver.GetDisplayPath(file);
            SystemDiagnosticsMessage = Loc.F(
                asJson
                    ? "app_settings.system_diagnostics_export_json_done"
                    : "app_settings.system_diagnostics_export_text_done",
                savedPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to export system diagnostics. Format {Format}", asJson ? "json" : "text");
            SystemDiagnosticsMessage = Loc.T("app_settings.system_diagnostics_export_failed");
        }
    }

    private static string BuildDiagnosticsSuggestedFileName(string extension)
    {
        var prefix = Loc.T("app_settings.system_diagnostics_export_prefix");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return $"{prefix}-{timestamp}.{extension}";
    }

    private static object BuildDiagnosticsExportPayload(
        AppDiagnosticsReportDto report,
        LocalBlockStorageMetricsDto? localMetrics)
    {
        return new
        {
            report.GeneratedAtUtc,
            report.TargetRepositoryId,
            report.TargetRepositoryName,
            report.TargetRepositoryPath,
            report.Process,
            report.History,
            report.Scan,
            report.NativeRuntime,
            LocalStorage = localMetrics
        };
    }

    private static string BuildDiagnosticsTextReport(
        AppDiagnosticsReportDto report,
        LocalBlockStorageMetricsDto? localMetrics)
    {
        var builder = new StringBuilder();
        var notAvailable = Loc.T("common.not_available_short");

        builder.AppendLine(Loc.T("app_settings.system_diagnostics_title"));
        builder.AppendLine(new string('=', 48));
        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_last_updated_label")}: {report.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"UTC: {report.GeneratedAtUtc:O}");
        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_target_repository_label")}: {(report.HasTargetRepository ? $"{report.TargetRepositoryName ?? notAvailable} ({report.TargetRepositoryPath ?? notAvailable})" : Loc.T("app_settings.system_diagnostics_no_repository"))}");
        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_native_runtime_label")}: {BuildNativeRuntimeExportLine(report.NativeRuntime)}");
        builder.AppendLine();

        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_metric_memory")}: {FormatBytes(report.Process.WorkingSetBytes)}");
        builder.AppendLine($"  Private bytes: {FormatBytes(report.Process.PrivateMemoryBytes)}");
        builder.AppendLine($"  Managed heap: {FormatBytes(report.Process.ManagedHeapBytes)}");
        builder.AppendLine($"  Target: {FormatBytes(report.Process.RecommendedLimitBytes)}");
        builder.AppendLine($"  Status: {(report.Process.MeetsRecommendedLimit ? "OK" : "Warning")}");
        builder.AppendLine();

        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_metric_history")}: {(report.History.Available ? $"{report.History.LatestEntriesLoadMs} ms" : notAvailable)}");
        builder.AppendLine($"  Snapshot history load: {report.History.SnapshotHistoryLoadMs} ms");
        builder.AppendLine($"  Latest entries count: {report.History.LatestEntriesCount}");
        builder.AppendLine($"  Target: {report.History.TargetMs} ms");
        builder.AppendLine($"  Status: {BuildAvailabilityStatus(report.History.Available, report.History.MeetsLatestEntriesTarget, report.History.ErrorMessage)}");
        builder.AppendLine();

        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_metric_scan")}: {(report.Scan.Available ? FormatThroughput(report.Scan.ThroughputMbPerSecond) : notAvailable)}");
        builder.AppendLine($"  Engine: {report.Scan.Engine}");
        builder.AppendLine($"  Duration: {report.Scan.DurationMs} ms");
        builder.AppendLine($"  Files: {report.Scan.FileCount}");
        builder.AppendLine($"  Data: {FormatBytes(report.Scan.TotalFileBytes)}");
        builder.AppendLine($"  Target: {report.Scan.TargetMbPerSecond:0.#} MB/s");
        builder.AppendLine($"  Status: {BuildAvailabilityStatus(report.Scan.Available, report.Scan.MeetsRecommendedTarget, report.Scan.ErrorMessage)}");
        builder.AppendLine();

        builder.AppendLine($"{Loc.T("app_settings.system_diagnostics_metric_dedup")}: {BuildDedupExportHeadline(localMetrics)}");
        if (localMetrics is not null)
        {
            builder.AppendLine($"  Referenced blocks: {localMetrics.ReferencedBlockCount}");
            builder.AppendLine($"  Unique blocks: {localMetrics.UniqueBlockCount}");
            builder.AppendLine($"  Missing blocks: {localMetrics.MissingBlockCount}");
            builder.AppendLine($"  Logical bytes: {FormatBytes(localMetrics.LogicalReferencedBytes)}");
            builder.AppendLine($"  Physical bytes: {FormatBytes(localMetrics.PhysicalStoredBytes)}");
            builder.AppendLine($"  Saved bytes: {FormatBytes(localMetrics.SavedBytes)}");
            builder.AppendLine($"  Repository count: {localMetrics.RepositoryCount}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildNativeRuntimeExportLine(AppDiagnosticsNativeRuntimeDto nativeRuntime)
    {
        return BuildNativeRuntimeSummary(nativeRuntime);
    }

    private static string BuildNativeRuntimeSummary(AppDiagnosticsNativeRuntimeDto nativeRuntime)
    {
        var headline = nativeRuntime.IsLoaded && nativeRuntime.IsHealthy && nativeRuntime.SupportsScan
            ? Loc.F(
                "app_settings.system_diagnostics_native_ok",
                string.IsNullOrWhiteSpace(nativeRuntime.LoadedPath)
                    ? Loc.T("common.not_available_short")
                    : nativeRuntime.LoadedPath)
            : Loc.F(
                "app_settings.system_diagnostics_native_warning",
                nativeRuntime.ErrorMessage ?? Loc.T("common.not_available_short"));

        if (nativeRuntime.FeatureUsage.Count == 0)
            return headline;

        var lines = new List<string> { headline, Loc.T("app_settings.system_diagnostics_native_session_usage") };
        lines.AddRange(nativeRuntime.FeatureUsage.Select(BuildNativeRuntimeFeatureUsageLine));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildNativeRuntimeFeatureUsageLine(AppDiagnosticsNativeFeatureUsageDto feature)
    {
        var featureName = BuildNativeRuntimeFeatureName(feature.FeatureKey);
        return feature.Supported
            ? Loc.F(
                "app_settings.system_diagnostics_native_feature_line",
                featureName,
                feature.NativeHits,
                feature.ManagedFallbacks)
            : Loc.F(
                "app_settings.system_diagnostics_native_feature_unavailable",
                featureName);
    }

    private static string BuildNativeRuntimeFeatureName(string featureKey)
        => featureKey switch
        {
            "scan" => Loc.T("app_settings.system_diagnostics_native_feature_scan"),
            "store_blocks" => Loc.T("app_settings.system_diagnostics_native_feature_store"),
            "restore_blocks" => Loc.T("app_settings.system_diagnostics_native_feature_restore"),
            "text_diff" => Loc.T("app_settings.system_diagnostics_native_feature_text_diff"),
            "snapshot_compare" => Loc.T("app_settings.system_diagnostics_native_feature_snapshot_compare"),
            "repository_path_compare" => Loc.T("app_settings.system_diagnostics_native_feature_repository_path_compare"),
            "version_planning" => Loc.T("app_settings.system_diagnostics_native_feature_version_planning"),
            "image_diff" => Loc.T("app_settings.system_diagnostics_native_feature_image_diff"),
            _ => featureKey
        };

    private static string BuildAvailabilityStatus(bool available, bool meetsTarget, string? errorMessage)
    {
        if (!available)
            return string.IsNullOrWhiteSpace(errorMessage) ? "Unavailable" : $"Unavailable: {errorMessage}";

        return meetsTarget ? "OK" : "Warning";
    }

    private static string BuildDedupExportHeadline(LocalBlockStorageMetricsDto? localMetrics)
    {
        if (localMetrics is null)
            return Loc.T("common.not_available_short");

        return $"{localMetrics.ReducedPercentFloor}%";
    }
}
