using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Sync;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class AuthDialogWindowViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly IUserProfileRepository _userProfiles;
    private readonly IRepositoryCloudSyncOrchestrator _sync;
    private readonly ILogger<AuthDialogWindowViewModel> _log;

    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(BusyTitle))]
    [NotifyPropertyChangedFor(nameof(BusyDetail))]
    [NotifyPropertyChangedFor(nameof(SubmitLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleModeLabel))]
    [NotifyPropertyChangedFor(nameof(IsConfirmPasswordVisible))]
    private bool _isRegisterMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _usernameInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _emailInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _passwordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _confirmPasswordInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    [ObservableProperty] private string _messageText = string.Empty;
    [ObservableProperty] private bool _isSuccessful;
    [ObservableProperty] private string _resultMessage = string.Empty;

    public AuthDialogWindowViewModel(
        IAuthService auth,
        IUserProfileRepository userProfiles,
        IRepositoryCloudSyncOrchestrator sync,
        ILogger<AuthDialogWindowViewModel> log)
    {
        _auth = auth;
        _userProfiles = userProfiles;
        _sync = sync;
        _log = log;

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(BusyTitle));
            OnPropertyChanged(nameof(BusyDetail));
            OnPropertyChanged(nameof(SubmitLabel));
            OnPropertyChanged(nameof(ToggleModeLabel));
            OnPropertyChanged(nameof(UsernamePlaceholderText));
            OnPropertyChanged(nameof(EmailPlaceholderText));
            OnPropertyChanged(nameof(PasswordPlaceholderText));
            OnPropertyChanged(nameof(ConfirmPasswordPlaceholderText));
            OnPropertyChanged(nameof(CancelButtonText));
        };
    }

    public string TitleText => IsRegisterMode
        ? Loc.T("auth_dialog.title_register")
        : Loc.T("auth_dialog.title_sign_in");

    public string SubtitleText => IsRegisterMode
        ? Loc.T("auth_dialog.subtitle_register")
        : Loc.T("auth_dialog.subtitle_sign_in");

    public string SubmitLabel => IsRegisterMode
        ? Loc.T("app_settings.auth_create_account")
        : Loc.T("auth.sign_in");

    public string BusyTitle => IsRegisterMode
        ? Loc.T("auth_dialog.loading_title_register")
        : Loc.T("auth_dialog.loading_title_sign_in");

    public string BusyDetail => IsRegisterMode
        ? Loc.T("auth_dialog.loading_detail_register")
        : Loc.T("auth_dialog.loading_detail_sign_in");

    public string ToggleModeLabel => IsRegisterMode
        ? Loc.T("app_settings.auth_switch_to_sign_in")
        : Loc.T("app_settings.auth_switch_to_registration");
    public string UsernamePlaceholderText => Loc.T("auth.username_placeholder");
    public string EmailPlaceholderText => Loc.T("auth.email_placeholder");
    public string PasswordPlaceholderText => Loc.T("auth.password_placeholder");
    public string ConfirmPasswordPlaceholderText => Loc.T("auth.confirm_password");
    public string CancelButtonText => Loc.T("common.cancel");

    public bool IsConfirmPasswordVisible => IsRegisterMode;

    public bool CanSubmit
    {
        get
        {
            if (IsBusy || string.IsNullOrWhiteSpace(UsernameInput) || string.IsNullOrWhiteSpace(PasswordInput))
                return false;

            if (!IsRegisterMode)
                return true;

            if (string.IsNullOrWhiteSpace(EmailInput))
                return false;

            return !string.IsNullOrWhiteSpace(ConfirmPasswordInput) &&
                   string.Equals(PasswordInput, ConfirmPasswordInput, StringComparison.Ordinal);
        }
    }

    public void Configure(bool registerMode)
    {
        IsRegisterMode = registerMode;
        UsernameInput = string.Empty;
        EmailInput = string.Empty;
        PasswordInput = string.Empty;
        ConfirmPasswordInput = string.Empty;
        MessageText = string.Empty;
        ResultMessage = string.Empty;
        IsSuccessful = false;
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        MessageText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        try
        {
            IsBusy = true;
            MessageText = string.Empty;

            if (IsRegisterMode && !string.Equals(PasswordInput, ConfirmPasswordInput, StringComparison.Ordinal))
            {
                MessageText = Loc.T("app_settings.auth_passwords_mismatch");
                return;
            }

            var username = UsernameInput.Trim();
            var email = EmailInput.Trim();
            var session = IsRegisterMode
                ? await _auth.RegisterAsync(username, email, PasswordInput)
                : await _auth.LoginAsync(username, PasswordInput);

            if (session is null)
            {
                MessageText = IsRegisterMode
                    ? Loc.T("app_settings.auth_registration_failed")
                    : Loc.T("app_settings.auth_login_failed");
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

            var tokenText = session.AccessTokenExpiresAtUtc.HasValue
                ? Loc.F("app_settings.auth_token_expires", session.AccessTokenExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;

            ResultMessage = IsRegisterMode
                ? Loc.F("app_settings.auth_registration_success", restored, tokenText)
                : Loc.F("app_settings.auth_login_success", restored, tokenText);

            IsSuccessful = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auth dialog request failed. Mode {Mode}", IsRegisterMode ? "register" : "login");
            MessageText = Loc.T("app_settings.auth_request_failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSuccessful = false;
        RequestClose?.Invoke();
    }
}
