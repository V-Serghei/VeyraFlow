using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Queries;
using Veyra.Desktop.Localization;

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

    public string Title => Loc.T(TitleKey);
    public string Subtitle => Loc.T(SubtitleKey);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}

public sealed record AppLanguageOptionItemViewModel(string Code, string DisplayName);

public sealed record AppUserProfileItemViewModel(
    string Username,
    bool IsActive,
    bool HasAccessToken,
    string LastLoginText,
    string CloudUserText,
    string CloudUserLabel,
    string TokenStateText);

public sealed partial class AppSettingsViewModel : ObservableObject
{
    private readonly IUserProfileRepository _userProfiles;
    private readonly IAccessTokenPolicyService _tokenPolicy;
    private readonly IAuthService _auth;
    private readonly IRepositoryCloudSyncOrchestrator _sync;
    private readonly IMediator _mediator;
    private readonly ILogger<AppSettingsViewModel> _log;
    private readonly LocalizationManager _localization;

    private bool _suppressLanguageSelectionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsUserTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSyncTabSelected))]
    private AppSettingsTabViewModel? _selectedTab;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _activeUsername = Loc.T("app_settings.not_signed_in");
    [ObservableProperty] private string _activeCloudUserText = Loc.T("common.not_available_short");
    [ObservableProperty] private string _activeSessionTokenState = Loc.T("app_settings.no_token");
    [ObservableProperty] private string _tokenPolicyHint = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    private string _usernameInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    private string _passwordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    private string _confirmPasswordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    [NotifyPropertyChangedFor(nameof(SubmitAuthLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleAuthLabel))]
    [NotifyPropertyChangedFor(nameof(IsConfirmPasswordVisible))]
    private bool _isRegisterMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAuthCommand))]
    private bool _isAuthBusy;

    [ObservableProperty] private string _authMessage = string.Empty;
    [ObservableProperty] private bool _isSyncBusy;
    [ObservableProperty] private string _syncMessage = string.Empty;
    [ObservableProperty] private string _cloudApiBaseUrl = string.Empty;
    [ObservableProperty] private int _localRepositoryCount;

    [ObservableProperty]
    private AppLanguageOptionItemViewModel? _selectedLanguage;

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

    public ObservableCollection<AppSettingsTabViewModel> Tabs { get; } =
    [
        new("general", "app_settings.tab_general_title", "app_settings.tab_general_subtitle"),
        new("user", "app_settings.tab_user_title", "app_settings.tab_user_subtitle"),
        new("sync", "app_settings.tab_sync_title", "app_settings.tab_sync_subtitle")
    ];

    public ObservableCollection<AppUserProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<AppLanguageOptionItemViewModel> Languages { get; } = [];

    public AppSettingsViewModel(
        IUserProfileRepository userProfiles,
        IAccessTokenPolicyService tokenPolicy,
        IAuthService auth,
        IRepositoryCloudSyncOrchestrator sync,
        IMediator mediator,
        IConfiguration config,
        ILogger<AppSettingsViewModel> log)
    {
        _userProfiles = userProfiles;
        _tokenPolicy = tokenPolicy;
        _auth = auth;
        _sync = sync;
        _mediator = mediator;
        _log = log;
        _localization = LocalizationManager.Instance;

        CloudApiBaseUrl = config["CloudApi:BaseUrl"]
                          ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                          ?? "http://localhost:8080";

        TokenPolicyHint = _tokenPolicy.GetPolicySummary();
        SelectedTab = Tabs.FirstOrDefault();

        _localization.LanguageChanged += OnLanguageChanged;
        RebuildLanguageOptions();
        UpdateLocalizationDiagnostics();
    }

    public bool IsGeneralTabSelected => string.Equals(SelectedTab?.Key, "general", StringComparison.OrdinalIgnoreCase);
    public bool IsUserTabSelected => string.Equals(SelectedTab?.Key, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsSyncTabSelected => string.Equals(SelectedTab?.Key, "sync", StringComparison.OrdinalIgnoreCase);

    public bool HasLocalizationIssues => LocalizationMissingKeysCount > 0 || LocalizationDuplicateKeysCount > 0 || LocalizationExtraKeysCount > 0;
    public bool HasLocalizationMissingSample => !string.IsNullOrWhiteSpace(LocalizationMissingSample);
    public bool HasLocalizationDuplicateSample => !string.IsNullOrWhiteSpace(LocalizationDuplicateSample);
    public bool HasLocalizationExtraSample => !string.IsNullOrWhiteSpace(LocalizationExtraSample);

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
    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            AuthMessage = string.Empty;
            SyncMessage = string.Empty;

            var active = await _userProfiles.GetActiveProfileAsync();
            var activeTokenState = _tokenPolicy.Evaluate(active?.AccessToken);

            ActiveUsername = active?.Username ?? Loc.T("app_settings.not_signed_in");
            ActiveCloudUserText = active?.CloudUserId?.ToString() ?? Loc.T("common.not_available_short");
            ActiveSessionTokenState = activeTokenState.Description;

            var profiles = await _userProfiles.GetProfilesAsync();
            Profiles.Clear();
            foreach (var p in profiles)
            {
                var tokenState = _tokenPolicy.Evaluate(p.AccessToken);
                var cloudUserText = p.CloudUserId?.ToString() ?? Loc.T("common.not_available_short");
                Profiles.Add(new AppUserProfileItemViewModel(
                    p.Username,
                    string.Equals(p.Username, active?.Username, StringComparison.OrdinalIgnoreCase),
                    !string.IsNullOrWhiteSpace(p.AccessToken),
                    p.LastLoginAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    cloudUserText,
                    Loc.F("app_settings.cloud_id_format", cloudUserText),
                    tokenState.Description));
            }

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            LocalRepositoryCount = repositories.Count;
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
    private void SelectTab(AppSettingsTabViewModel? tab)
    {
        if (tab is not null)
            SelectedTab = tab;
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
            var session = IsRegisterMode
                ? await _auth.RegisterAsync(username, PasswordInput)
                : await _auth.LoginAsync(username, PasswordInput);

            if (session is null)
            {
                AuthMessage = IsRegisterMode
                    ? Loc.T("app_settings.auth_registration_failed")
                    : Loc.T("app_settings.auth_login_failed");
                return;
            }

            await _userProfiles.SaveOrUpdateProfileAsync(
                session.Username,
                session.CloudUserId,
                session.AccessToken);

            var restored = await _sync.RestoreRepositoriesFromCloudAsync();
            await _sync.ProcessPendingQueueAsync();

            PasswordInput = string.Empty;
            ConfirmPasswordInput = string.Empty;

            var tokenText = session.AccessTokenExpiresAtUtc.HasValue
                ? Loc.F("app_settings.auth_token_expires", session.AccessTokenExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

            AuthMessage = IsRegisterMode
                ? Loc.F("app_settings.auth_registration_success", restored, tokenText)
                : Loc.F("app_settings.auth_login_success", restored, tokenText);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auth submit failed in app settings");
            AuthMessage = Loc.T("app_settings.auth_request_failed");
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
            AuthMessage = Loc.F("app_settings.profile_switched", profile.Username);
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
            await _userProfiles.SignOutActiveAsync();
            AuthMessage = Loc.T("app_settings.signed_out");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sign-out failed");
            AuthMessage = Loc.T("app_settings.sign_out_failed");
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
            IsSyncBusy = true;
            SyncMessage = string.Empty;

            var restored = await _sync.RestoreRepositoriesFromCloudAsync();
            SyncMessage = Loc.F("app_settings.sync_restore_finished", restored);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cloud restore failed");
            SyncMessage = Loc.T("app_settings.sync_restore_failed");
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
            IsSyncBusy = true;
            SyncMessage = string.Empty;

            await _sync.ProcessPendingQueueAsync();
            SyncMessage = Loc.T("app_settings.sync_queue_processed");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Processing sync queue failed");
            SyncMessage = Loc.T("app_settings.sync_queue_failed");
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
            IsSyncBusy = true;
            SyncMessage = string.Empty;

            var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
            foreach (var repository in repositories)
                await _sync.TryPushLatestSnapshotAsync(repository.Id);

            await _sync.ProcessPendingQueueAsync();

            SyncMessage = Loc.F("app_settings.sync_push_requested", repositories.Count);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Push all repositories to cloud failed");
            SyncMessage = Loc.T("app_settings.sync_push_failed");
        }
        finally
        {
            IsSyncBusy = false;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        foreach (var tab in Tabs)
            tab.RefreshLocalization();

        RebuildLanguageOptions();
        UpdateLocalizationDiagnostics();

        OnPropertyChanged(nameof(SubmitAuthLabel));
        OnPropertyChanged(nameof(ToggleAuthLabel));

        _ = LoadAsync();
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
}
