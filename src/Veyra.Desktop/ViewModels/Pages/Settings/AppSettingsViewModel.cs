using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.DTOs;
using Veyra.Application.Queries;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Styling;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views.Windows;

namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed partial class AppSettingsTabViewModel : ObservableObject
{
    public AppSettingsTabViewModel(string key, string titleKey, string subtitleKey)
    {
        Key = key;
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
    }

    public string Key { get; }
    public string TitleKey { get; }
    public string SubtitleKey { get; }
    [ObservableProperty] private bool _isSelected;

    public string Title => Loc.T(TitleKey);
    public string Subtitle => Loc.T(SubtitleKey);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}

public sealed record AppLanguageOptionItemViewModel(string Code, string DisplayName);
public sealed record AppThemeOptionItemViewModel(string Code, string DisplayName);
public sealed record AppExperienceOptionItemViewModel(string Code, string DisplayName, string Description);

public sealed record AppUserProfileItemViewModel(
    string Username,
    string EmailText,
    bool IsActive,
    bool HasAccessToken,
    string LastLoginText,
    string CloudUserText,
    string CloudUserLabel,
    string TokenStateText);

public sealed record AppOperationJournalItemViewModel(
    string TimestampText,
    string Level,
    string Category,
    string Action,
    string ScopeText,
    string Message);

public sealed record AppRepositorySyncIssueItemViewModel(
    int RepositoryId,
    string Name,
    string StatusText,
    string QueueText,
    string LastSyncText,
    bool HasProgress,
    int ProgressCurrent,
    int ProgressTotal,
    double ProgressPercent,
    string ProgressText,
    string EtaText,
    string ElapsedText,
    string FinishAtText,
    string LastProgressUpdateText,
    string StallText,
    bool HasStall,
    string ErrorText,
    bool HasError);

public sealed partial class AppSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan SyncStatusAutoRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SyncProgressStallThreshold = TimeSpan.FromSeconds(90);

    private readonly IUserProfileRepository _userProfiles;
    private readonly IAccessTokenPolicyService _tokenPolicy;
    private readonly IAuthService _auth;
    private readonly IOperationJournalService _journal;
    private readonly IRepositoryCloudSyncOrchestrator _sync;
    private readonly ICloudSyncService _cloudSyncService;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly IWindowService _windows;
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
    [ObservableProperty] private bool _isExperienceModeChangeInProgress;
    private bool _hasActiveSyncWork;
    private readonly Dictionary<int, bool> _stallStateByRepositoryId = [];
    private CancellationTokenSource? _syncStatusAutoRefreshCts;
    private Task? _syncStatusAutoRefreshTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsUserTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncTabSelected))]
    private AppSettingsTabViewModel? _selectedTab;

    [ObservableProperty] private bool _isLoading;
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
    [ObservableProperty] private bool _isSyncBusy;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositorySyncIssues))]
    private int _repositorySyncIssueCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSensitiveActionVerification))]
    private bool _hasActiveProfile;
    [ObservableProperty] private bool _requirePasswordForSensitiveActions;

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
        new("sync", "app_settings.tab_sync_title", "app_settings.tab_sync_subtitle")
    ];

    public ObservableCollection<AppUserProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<AppLanguageOptionItemViewModel> Languages { get; } = [];
    public ObservableCollection<AppThemeOptionItemViewModel> Themes { get; } = [];
    public ObservableCollection<AppExperienceOptionItemViewModel> ExperienceModes { get; } = [];
    public ObservableCollection<AppOperationJournalItemViewModel> OperationJournalItems { get; } = [];
    public ObservableCollection<AppRepositorySyncIssueItemViewModel> RepositorySyncIssues { get; } = [];

    public AppSettingsViewModel(
        IUserProfileRepository userProfiles,
        IAccessTokenPolicyService tokenPolicy,
        IAuthService auth,
        IOperationJournalService journal,
        IRepositoryCloudSyncOrchestrator sync,
        ICloudSyncService cloudSyncService,
        OnboardingStateService onboardingState,
        ISensitiveActionGuard sensitiveActionGuard,
        IWindowService windows,
        IMediator mediator,
        IConfiguration config,
        ILogger<AppSettingsViewModel> log)
    {
        _userProfiles = userProfiles;
        _tokenPolicy = tokenPolicy;
        _auth = auth;
        _journal = journal;
        _sync = sync;
        _cloudSyncService = cloudSyncService;
        _onboardingState = onboardingState;
        _sensitiveActionGuard = sensitiveActionGuard;
        _windows = windows;
        _mediator = mediator;
        _log = log;
        _localization = LocalizationManager.Instance;
        _theme = ThemeManager.Instance;
        _experience = UserExperienceManager.Instance;

        CloudApiBaseUrl = config["CloudApi:BaseUrl"]
                          ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                          ?? "http://localhost:8080";

        TokenPolicyHint = LocalizeUserFacingMessage(_tokenPolicy.GetPolicySummary(), "common.not_available_short");
        ClearCloudStorageMetrics();
        SelectedTab = Tabs.FirstOrDefault();

        _localization.LanguageChanged += OnLanguageChanged;
        _theme.ThemeChanged += OnThemeChanged;
        _experience.ModeChanged += OnExperienceModeChanged;
        RebuildLanguageOptions();
        RebuildThemeOptions();
        RebuildExperienceOptions();
        UpdateLocalizationDiagnostics();
    }

    public bool IsGeneralTabSelected => string.Equals(SelectedTab?.Key, "general", StringComparison.OrdinalIgnoreCase);
    public bool IsUserTabSelected => string.Equals(SelectedTab?.Key, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsSyncTabSelected => string.Equals(SelectedTab?.Key, "sync", StringComparison.OrdinalIgnoreCase);

    public bool HasLocalizationIssues => LocalizationMissingKeysCount > 0 || LocalizationDuplicateKeysCount > 0 || LocalizationExtraKeysCount > 0;
    public bool HasLocalizationMissingSample => !string.IsNullOrWhiteSpace(LocalizationMissingSample);
    public bool HasLocalizationDuplicateSample => !string.IsNullOrWhiteSpace(LocalizationDuplicateSample);
    public bool HasLocalizationExtraSample => !string.IsNullOrWhiteSpace(LocalizationExtraSample);
    public bool CanEditSensitiveActionVerification => HasActiveProfile;
    public bool HasRepositorySyncIssues => RepositorySyncIssueCount > 0;
    public bool HasKnownProfiles => Profiles.Count > 0;
    public bool IsGuestMode => !HasActiveProfile;
    public bool HasOperationJournalItems => OperationJournalItems.Count > 0;
    public bool IsBasicMode => _experience.IsBasicMode;
    public bool IsProfessionalMode => _experience.IsProfessionalMode;
    public bool ShowLocalizationDiagnostics => IsProfessionalMode;
    public bool ShowTechnicalCloudDetails => IsProfessionalMode;
    public bool ShowTechnicalProfileDetails => IsProfessionalMode;
    public bool ShowCloudStorageDiagnostics => IsProfessionalMode;
    public bool ShowDetailedRepositorySyncIssueDiagnostics => IsProfessionalMode;
    public string ExperienceModeHint => IsBasicMode
        ? Loc.T("app_settings.experience_basic_hint")
        : Loc.T("app_settings.experience_professional_hint");
    public string LocalizationSummaryText => HasLocalizationIssues
        ? Loc.T("app_settings.localization_user_warning")
        : Loc.T("app_settings.localization_user_ok");
    public string SyncSectionIntroText => IsBasicMode
        ? Loc.T("app_settings.sync_intro_basic")
        : Loc.T("app_settings.sync_intro_professional");
    public string SyncSectionHelpText => IsBasicMode
        ? Loc.T("app_settings.sync_help_basic")
        : Loc.T("app_settings.sync_help_professional");
    public string CloudStorageHelpText => IsBasicMode
        ? Loc.T("app_settings.cloud_storage_help_basic")
        : Loc.T("app_settings.cloud_storage_help_professional");
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

        _localization.SetLanguage(value.Code);

    }

    partial void OnSelectedThemeChanged(AppThemeOptionItemViewModel? value)
    {
        if (_suppressThemeSelectionChanged || value is null)
            return;

        _theme.SetTheme(value.Code);
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

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            AuthMessage = string.Empty;
            SyncMessage = string.Empty;
            GeneralMessage = string.Empty;
            _log.LogInformation("Loading app settings");

            var active = await _userProfiles.GetActiveProfileAsync();
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

            var profiles = await _userProfiles.GetProfilesAsync();
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

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            LocalRepositoryCount = repositories.Count;
            _hasActiveSyncWork = RefreshRepositorySyncIssues(repositories);
            UpdateSyncStatusAutoRefreshState();

            await LoadCloudStorageMetricsAsync(active, silent: true);

            await LoadOperationJournalAsync();
            _log.LogInformation(
                "App settings loaded. Profiles {Profiles}. Repositories {Repositories}. SyncIssues {SyncIssues}",
                Profiles.Count,
                LocalRepositoryCount,
                RepositorySyncIssueCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load app settings state");
            SyncMessage = Loc.T("app_settings.error_load_state");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void ReplayGuidedTour()
    {
        _log.LogInformation("Guided tour replay requested from app settings");
        _onboardingState.RequestFirstRunTour();
        GeneralMessage = Loc.T("app_settings.guided_tour_replay_started");
    }

    public void SelectTabByKey(string key)
    {
        var tab = Tabs.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
            SelectTab(tab);
    }

    [RelayCommand]
    private async Task OpenRepositorySyncIssueSettingsAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null || OpenRepositorySettingsRequested is null)
            return;

        _log.LogInformation("Opening repository settings from sync issues. RepositoryId {RepositoryId}", issue.RepositoryId);
        await OpenRepositorySettingsRequested.Invoke(issue.RepositoryId);
    }

    [RelayCommand]
    private async Task RetryRepositorySyncIssueAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null)
            return;

        try
        {
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
        if (tab is not null)
            SelectedTab = tab;
    }

    partial void OnSelectedTabChanged(AppSettingsTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsSelected = ReferenceEquals(tab, value);

        UpdateSyncStatusAutoRefreshState();
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

            var restored = await _sync.RestoreRepositoriesFromCloudAsync();
            await _sync.ProcessPendingQueueAsync();

            PasswordInput = string.Empty;
            ConfirmPasswordInput = string.Empty;
            if (IsRegisterMode)
                EmailInput = string.Empty;

            var tokenText = session.AccessTokenExpiresAtUtc.HasValue
                ? Loc.F("app_settings.auth_token_expires", session.AccessTokenExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

            AuthMessage = IsRegisterMode
                ? Loc.F("app_settings.auth_registration_success", restored, tokenText)
                : Loc.F("app_settings.auth_login_success", restored, tokenText);

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
            AuthMessage = Loc.T("app_settings.auth_request_failed");
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

            await _sync.ProcessPendingQueueAsync();
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
        try
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
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open operation journal window");
            GeneralMessage = Loc.T("app_settings.operation_journal_open_failed");
        }
    }

    [RelayCommand]
    private async Task RestoreFromCloudAsync()
    {
        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredLocalizedAsync(
                "security.action_cloud_sync",
                "security.action_cloud_restore_body");

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;
            await AppendJournalAsync("info", "sync", "settings_restore_from_cloud", "Operation started.", ActiveUsername);

            var restored = await _sync.RestoreRepositoriesFromCloudAsync();
            SyncMessage = Loc.F("app_settings.sync_restore_finished", restored);
            await AppendJournalAsync("info", "sync", "settings_restore_from_cloud", SyncMessage, ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cloud restore failed");
            SyncMessage = Loc.T("app_settings.sync_restore_failed");
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
            SyncMessage = Loc.T("app_settings.sync_queue_processed");
            await AppendJournalAsync("info", "sync", "settings_process_queue", SyncMessage, ActiveUsername);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Processing sync queue failed");
            SyncMessage = Loc.T("app_settings.sync_queue_failed");
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
            SyncMessage = Loc.T("app_settings.sync_push_failed");
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
            IsCloudMaintenanceBusy = true;
            CloudStorageMessage = string.Empty;
            _log.LogInformation("Refreshing sync section status and cloud storage metrics");
            await AppendJournalAsync("info", "sync", "settings_cloud_storage_refresh", "Operation started.", ActiveUsername);

            await RefreshSyncSectionAsync(silentMetrics: false, refreshStorageMetrics: true);
            _log.LogInformation(
                "Sync section refresh finished. HasMetrics {HasMetrics}. SyncIssues {SyncIssues}. ActiveWork {ActiveWork}",
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

    [RelayCommand]
    private async Task RepairCloudStorageAsync()
    {
        try
        {
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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RebuildThemeOptions();
    }

    private void OnExperienceModeChanged(object? sender, EventArgs e)
    {
        RebuildExperienceOptions();
        OnPropertyChanged(nameof(IsBasicMode));
        OnPropertyChanged(nameof(IsProfessionalMode));
        OnPropertyChanged(nameof(ShowLocalizationDiagnostics));
        OnPropertyChanged(nameof(ShowTechnicalCloudDetails));
        OnPropertyChanged(nameof(ShowTechnicalProfileDetails));
        OnPropertyChanged(nameof(ShowCloudStorageDiagnostics));
        OnPropertyChanged(nameof(ShowDetailedRepositorySyncIssueDiagnostics));
        OnPropertyChanged(nameof(ExperienceModeHint));
        OnPropertyChanged(nameof(LocalizationSummaryText));
        OnPropertyChanged(nameof(SyncSectionIntroText));
        OnPropertyChanged(nameof(SyncSectionHelpText));
        OnPropertyChanged(nameof(CloudStorageHelpText));
        OnPropertyChanged(nameof(RepositorySyncHealthHelpText));
        OnPropertyChanged(nameof(RepositorySyncProgressHelpText));
        OnPropertyChanged(nameof(RestoreFromCloudLabel));
        OnPropertyChanged(nameof(PushAllRepositoriesLabel));
        OnPropertyChanged(nameof(ProcessQueueLabel));
        _ = LoadOperationJournalAsync();
        _ = RefreshSyncSectionAsync(silentMetrics: true, refreshStorageMetrics: false, CancellationToken.None);
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

        TokenPolicyHint = LocalizeUserFacingMessage(_tokenPolicy.GetPolicySummary(), "common.not_available_short");
        RebuildLanguageOptions();
        RebuildThemeOptions();
        RebuildExperienceOptions();
        UpdateLocalizationDiagnostics();
        OnPropertyChanged(nameof(IsBasicMode));
        OnPropertyChanged(nameof(IsProfessionalMode));
        OnPropertyChanged(nameof(ShowLocalizationDiagnostics));
        OnPropertyChanged(nameof(ShowTechnicalCloudDetails));
        OnPropertyChanged(nameof(ShowTechnicalProfileDetails));
        OnPropertyChanged(nameof(ShowCloudStorageDiagnostics));
        OnPropertyChanged(nameof(ShowDetailedRepositorySyncIssueDiagnostics));
        OnPropertyChanged(nameof(ExperienceModeHint));
        OnPropertyChanged(nameof(LocalizationSummaryText));
        OnPropertyChanged(nameof(SyncSectionIntroText));
        OnPropertyChanged(nameof(SyncSectionHelpText));
        OnPropertyChanged(nameof(CloudStorageHelpText));
        OnPropertyChanged(nameof(RepositorySyncHealthHelpText));
        OnPropertyChanged(nameof(RepositorySyncProgressHelpText));
        OnPropertyChanged(nameof(RestoreFromCloudLabel));
        OnPropertyChanged(nameof(PushAllRepositoriesLabel));
        OnPropertyChanged(nameof(ProcessQueueLabel));

        OnPropertyChanged(nameof(SubmitAuthLabel));
        OnPropertyChanged(nameof(ToggleAuthLabel));
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
            var active = await _userProfiles.GetActiveProfileAsync(ct);
            var repositories = await _mediator.Send(new GetAllRepositoriesQuery(), ct);

            LocalRepositoryCount = repositories.Count;
            _hasActiveSyncWork = RefreshRepositorySyncIssues(repositories);

            if (refreshStorageMetrics)
                await LoadCloudStorageMetricsAsync(active, silent: silentMetrics);

            UpdateSyncStatusAutoRefreshState();
        }
        finally
        {
            _syncStatusRefreshGate.Release();
        }
    }

    private async Task LoadCloudStorageMetricsAsync(UserProfileSessionDto? activeProfile, bool silent)
    {
        if (!TryResolveCloudAccessToken(activeProfile, silent, out var accessToken))
            return;

        CloudStorageMetricsDto? metrics;
        try
        {
            metrics = await _cloudSyncService.GetStorageMetricsAsync(accessToken);
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

    private void ApplyCloudStorageMetrics(CloudStorageMetricsDto metrics)
    {
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
    }

    private bool RefreshRepositorySyncIssues(IReadOnlyList<RepositoryDto> repositories)
    {
        RepositorySyncIssues.Clear();
        var hasActiveWork = false;
        var currentStallStates = new Dictionary<int, bool>();

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

            currentStallStates[repository.Id] = isStalled;
            LogStallTransition(repository, isStalled, stallText);

            RepositorySyncIssues.Add(new AppRepositorySyncIssueItemViewModel(
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
                !string.IsNullOrWhiteSpace(cloud?.LastError)));

            hasActiveWork |= HasActiveSyncWork(cloud);
        }

        _stallStateByRepositoryId.Clear();
        foreach (var pair in currentStallStates)
            _stallStateByRepositoryId[pair.Key] = pair.Value;

        RepositorySyncIssueCount = RepositorySyncIssues.Count;
        return hasActiveWork;
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
        return normalizedStatus is "queued"
            or "syncing"
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
        return normalizedStatus is "queued"
            or "syncing"
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
        if (normalizedStatus is not ("syncing_upload" or "syncing" or "retrying" or "offline_retry"))
            return false;

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

    private static string FormatCloudSyncStatus(string? status)
    {
        if (UserExperienceManager.Instance.IsBasicMode)
        {
            if (string.IsNullOrWhiteSpace(status))
                return Loc.T("repo_settings.sync_status_basic_local");

            var normalizedBasic = status.Trim().ToLowerInvariant();
            return normalizedBasic switch
            {
                "queued" or "syncing" or "offline_retry" or "retrying" => Loc.T("repo_settings.sync_status_basic_working"),
                "auth_required" or "conflict" or "failed" or "dead_letter" => Loc.T("repo_settings.sync_status_basic_attention"),
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
            "syncing" => Loc.T("dashboard.sync.syncing"),
            _ when normalized.StartsWith("syncing_upload", StringComparison.Ordinal) => FormatSyncingUploadStatus(status),
            "offline_retry" => Loc.T("dashboard.sync.offline_retry"),
            "retrying" => Loc.T("dashboard.sync.retrying"),
            "auth_required" => Loc.T("dashboard.sync.auth_required"),
            "conflict" => Loc.T("dashboard.sync.conflict"),
            "failed" => Loc.T("dashboard.sync.failed"),
            "skipped" => Loc.T("dashboard.sync.skipped"),
            _ when normalized.StartsWith("synced", StringComparison.Ordinal) => Loc.T("dashboard.sync.synced"),
            _ => status.Replace('_', ' ')
        };
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
        if (UserExperienceManager.Instance.IsBasicMode)
            return pending == 0 && running == 0 && retry == 0 && conflict == 0 && deadLetter == 0
                ? Loc.T("repo_settings.queue_basic_idle")
                : Loc.T("repo_settings.queue_basic_active");

        return Loc.F("dashboard.queue_summary", pending, running, retry, conflict, deadLetter);
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

    private void ClearCloudStorageMetrics()
    {
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

            SelectedLanguage = Languages.FirstOrDefault(x =>
                string.Equals(x.Code, _localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressLanguageSelectionChanged = false;
        }
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

            SelectedTheme = Themes.FirstOrDefault(x =>
                string.Equals(x.Code, _theme.CurrentThemeCode, StringComparison.OrdinalIgnoreCase));
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

    private async Task LoadOperationJournalAsync()
    {
        try
        {
            var entries = await _journal.GetRecentAsync(5);
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
            "CreateRepositoryWithFormatsCommand" => Loc.T("operation_journal.action.create_repository"),
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

    private static bool IsCloudConnectivityFailure(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
            return true;

        return ex.InnerException is not null && IsCloudConnectivityFailure(ex.InnerException);
    }
}

