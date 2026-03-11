using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
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
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Styling;

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
    string ErrorText,
    bool HasError);

public sealed partial class AppSettingsViewModel : ObservableObject
{
    private readonly IUserProfileRepository _userProfiles;
    private readonly IAccessTokenPolicyService _tokenPolicy;
    private readonly IAuthService _auth;
    private readonly IOperationJournalService _journal;
    private readonly IRepositoryCloudSyncOrchestrator _sync;
    private readonly ICloudSyncService _cloudSyncService;
    private readonly ISensitiveActionGuard _sensitiveActionGuard;
    private readonly IMediator _mediator;
    private readonly ILogger<AppSettingsViewModel> _log;
    private readonly LocalizationManager _localization;
    private readonly ThemeManager _theme;

    private bool _suppressLanguageSelectionChanged;
    private bool _suppressThemeSelectionChanged;
    private bool _suppressSensitiveActionToggleChanged;

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

    public ObservableCollection<AppSettingsTabViewModel> Tabs { get; } =
    [
        new("general", "app_settings.tab_general_title", "app_settings.tab_general_subtitle"),
        new("user", "app_settings.tab_user_title", "app_settings.tab_user_subtitle"),
        new("sync", "app_settings.tab_sync_title", "app_settings.tab_sync_subtitle")
    ];

    public ObservableCollection<AppUserProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<AppLanguageOptionItemViewModel> Languages { get; } = [];
    public ObservableCollection<AppThemeOptionItemViewModel> Themes { get; } = [];
    public ObservableCollection<AppOperationJournalItemViewModel> OperationJournalItems { get; } = [];
    public ObservableCollection<AppRepositorySyncIssueItemViewModel> RepositorySyncIssues { get; } = [];

    public AppSettingsViewModel(
        IUserProfileRepository userProfiles,
        IAccessTokenPolicyService tokenPolicy,
        IAuthService auth,
        IOperationJournalService journal,
        IRepositoryCloudSyncOrchestrator sync,
        ICloudSyncService cloudSyncService,
        ISensitiveActionGuard sensitiveActionGuard,
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
        _sensitiveActionGuard = sensitiveActionGuard;
        _mediator = mediator;
        _log = log;
        _localization = LocalizationManager.Instance;
        _theme = ThemeManager.Instance;

        CloudApiBaseUrl = config["CloudApi:BaseUrl"]
                          ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                          ?? "http://localhost:8080";

        TokenPolicyHint = _tokenPolicy.GetPolicySummary();
        ClearCloudStorageMetrics();
        SelectedTab = Tabs.FirstOrDefault();

        _localization.LanguageChanged += OnLanguageChanged;
        _theme.ThemeChanged += OnThemeChanged;
        RebuildLanguageOptions();
        RebuildThemeOptions();
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

            var active = await _userProfiles.GetActiveProfileAsync();
            var activeTokenState = _tokenPolicy.Evaluate(active?.AccessToken);
            HasActiveProfile = active is not null;

            ActiveUsername = active?.Username ?? Loc.T("app_settings.not_signed_in");
            ActiveEmail = string.IsNullOrWhiteSpace(active?.Email)
                ? Loc.T("common.not_available_short")
                : active.Email!;
            ActiveCloudUserText = active?.CloudUserId?.ToString() ?? Loc.T("common.not_available_short");
            ActiveSessionTokenState = activeTokenState.Description;

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
                    tokenState.Description));
            }

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            LocalRepositoryCount = repositories.Count;
            RefreshRepositorySyncIssues(repositories);

            await LoadCloudStorageMetricsAsync(active, silent: true);

            await LoadOperationJournalAsync();
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
    private async Task OpenRepositorySyncIssueSettingsAsync(AppRepositorySyncIssueItemViewModel? issue)
    {
        if (issue is null || OpenRepositorySettingsRequested is null)
            return;

        await OpenRepositorySettingsRequested.Invoke(issue.RepositoryId);
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
    }

    [RelayCommand]
    private void ToggleAuthMode()
    {
        IsRegisterMode = !IsRegisterMode;
        AuthMessage = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSubmitAuth))]
    private async Task SubmitAuthAsync()
    {
        try
        {
            IsAuthBusy = true;
            AuthMessage = string.Empty;

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
            AuthMessage = Loc.T("app_settings.profile_already_active");
            return;
        }

        try
        {
            IsAuthBusy = true;
            AuthMessage = string.Empty;

            var switched = await _userProfiles.SetActiveProfileAsync(profile.Username);
            if (!switched)
            {
                AuthMessage = Loc.T("app_settings.profile_not_found");
                return;
            }

            await _sync.ProcessPendingQueueAsync();
            AuthMessage = profile.HasAccessToken
                ? Loc.F("app_settings.profile_switched", profile.Username)
                : Loc.F("app_settings.profile_switched_without_token", profile.Username);
            await LoadAsync();
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
            var activeProfile = await _userProfiles.GetActiveProfileAsync();
            if (activeProfile is null)
            {
                AuthMessage = Loc.T("app_settings.sign_out_not_signed_in");
                return;
            }

            if (!string.IsNullOrWhiteSpace(activeProfile?.AccessToken))
            {
                try
                {
                    var cloudLoggedOut = await _auth.LogoutAsync(activeProfile.AccessToken);
                    if (!cloudLoggedOut)
                        _log.LogInformation("Cloud logout returned non-success for user {Username}", activeProfile.Username);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cloud logout failed for user {Username}; continuing local sign-out", activeProfile.Username);
                }
            }

            await _userProfiles.SignOutActiveAsync();
            AuthMessage = Loc.T("app_settings.signed_out");
            await AppendJournalAsync("info", "auth", "settings_sign_out", AuthMessage, ActiveUsername);
            await LoadAsync();
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
    private async Task RestoreFromCloudAsync()
    {
        try
        {
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_cloud_sync"),
                Loc.T("security.action_cloud_restore_body"));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;

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
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_cloud_sync"),
                Loc.T("security.action_process_queue_body"));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;

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
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_cloud_sync"),
                Loc.T("security.action_push_all_body"));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    SyncMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsSyncBusy = true;
            SyncMessage = string.Empty;

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

            var active = await _userProfiles.GetActiveProfileAsync();
            await LoadCloudStorageMetricsAsync(active, silent: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to refresh cloud storage metrics");
            CloudStorageMessage = Loc.T("app_settings.cloud_storage_metrics_failed");
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
            var guardResult = await _sensitiveActionGuard.AuthorizeIfRequiredAsync(
                Loc.T("security.action_cloud_storage_repair"),
                Loc.T("security.action_cloud_storage_repair_body"));

            if (!guardResult.IsAllowed)
            {
                if (!guardResult.IsCancelled)
                    CloudStorageMessage = guardResult.ErrorMessage ?? Loc.T("security.error_verification_failed");
                return;
            }

            IsCloudMaintenanceBusy = true;
            CloudStorageMessage = string.Empty;

            var active = await _userProfiles.GetActiveProfileAsync();
            var accessToken = active?.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ClearCloudStorageMetrics();
                CloudStorageMessage = Loc.T("app_settings.cloud_storage_sign_in_required");
                return;
            }

            var result = await _cloudSyncService.RepairStorageAsync(accessToken);
            if (result is null)
            {
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

    private void RefreshLocalizationState()
    {
        foreach (var tab in Tabs)
            tab.RefreshLocalization();

        RebuildLanguageOptions();
        RebuildThemeOptions();
        UpdateLocalizationDiagnostics();

        OnPropertyChanged(nameof(SubmitAuthLabel));
        OnPropertyChanged(nameof(ToggleAuthLabel));

        _ = LoadAsync();
    }

    private async Task LoadCloudStorageMetricsAsync(UserProfileSessionDto? activeProfile, bool silent)
    {
        var accessToken = activeProfile?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ClearCloudStorageMetrics();
            if (!silent)
                CloudStorageMessage = Loc.T("app_settings.cloud_storage_sign_in_required");
            return;
        }

        var metrics = await _cloudSyncService.GetStorageMetricsAsync(accessToken);
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

    private void RefreshRepositorySyncIssues(IReadOnlyList<RepositoryDto> repositories)
    {
        RepositorySyncIssues.Clear();

        foreach (var repository in repositories
                     .Where(HasSyncIssue)
                     .OrderByDescending(r => GetIssueSeverity(r.CloudSync))
                     .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cloud = repository.CloudSync;
            var lastSyncText = cloud?.LastSyncedAtUtc is null
                ? Loc.T("common.never")
                : cloud.LastSyncedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

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
                cloud?.LastError ?? string.Empty,
                !string.IsNullOrWhiteSpace(cloud?.LastError)));
        }

        RepositorySyncIssueCount = RepositorySyncIssues.Count;
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

    private static string FormatCloudQueueSummary(int pending, int running, int retry, int conflict, int deadLetter)
        => Loc.F("dashboard.queue_summary", pending, running, retry, conflict, deadLetter);

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

    private async Task LoadOperationJournalAsync()
    {
        try
        {
            var entries = await _journal.GetRecentAsync(120);
            OperationJournalItems.Clear();

            foreach (var entry in entries)
            {
                var scope = entry.RepositoryId.HasValue
                    ? $"repo:{entry.RepositoryId.Value}"
                    : string.IsNullOrWhiteSpace(entry.Username)
                        ? "-"
                        : entry.Username!;

                OperationJournalItems.Add(new AppOperationJournalItemViewModel(
                    entry.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    entry.Level,
                    entry.Category,
                    entry.Action,
                    scope,
                    entry.Message));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load operation journal");
            OperationJournalItems.Clear();
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
}

