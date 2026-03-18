using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Veyra.Application.Commands.Auth;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.AuthWindow;

public partial class LoginViewModel : ObservableObject
{
    private readonly IMediator _mediator;

    public event System.Action<bool>? AuthCompleted;
    public event System.Action? GuestModeRequested;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRegisterMode;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand RegisterCommand { get; }
    public IRelayCommand ToggleModeCommand { get; }
    public IRelayCommand ContinueAsGuestCommand { get; }

    public string ActionTitle => IsRegisterMode ? Loc.T("login.action_title_register") : Loc.T("login.action_title_sign_in");
    public string SubmitLabel => IsRegisterMode ? Loc.T("auth.register") : Loc.T("auth.sign_in");
    public string BusyTitle => IsRegisterMode ? Loc.T("login.loading_title_register") : Loc.T("login.loading_title_sign_in");
    public string BusyDetail => IsRegisterMode ? Loc.T("login.loading_detail_register") : Loc.T("login.loading_detail_sign_in");
    public string ToggleLabel => IsRegisterMode
        ? Loc.T("login.toggle_to_sign_in")
        : Loc.T("login.toggle_to_register");
    public string GuestModeLabel => Loc.T("login.continue_as_guest");
    public string GuestModeHint => Loc.T("login.guest_mode_hint");

    public LoginViewModel(IMediator mediator)
    {
        _mediator = mediator;
        LoginCommand = new AsyncRelayCommand(DoLoginAsync);
        RegisterCommand = new AsyncRelayCommand(DoRegisterAsync);
        ToggleModeCommand = new RelayCommand(ToggleMode);
        ContinueAsGuestCommand = new RelayCommand(ContinueAsGuest);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActionTitle));
            OnPropertyChanged(nameof(SubmitLabel));
            OnPropertyChanged(nameof(BusyTitle));
            OnPropertyChanged(nameof(BusyDetail));
            OnPropertyChanged(nameof(ToggleLabel));
            OnPropertyChanged(nameof(GuestModeLabel));
            OnPropertyChanged(nameof(GuestModeHint));
        };
    }

    partial void OnIsRegisterModeChanged(bool value)
    {
        Error = string.Empty;
        OnPropertyChanged(nameof(ActionTitle));
        OnPropertyChanged(nameof(SubmitLabel));
        OnPropertyChanged(nameof(BusyTitle));
        OnPropertyChanged(nameof(BusyDetail));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
    }

    private void ContinueAsGuest()
    {
        if (IsBusy)
            return;

        Error = string.Empty;
        GuestModeRequested?.Invoke();
    }

    private async Task DoLoginAsync()
    {
        try
        {
            IsBusy = true;
            Error = string.Empty;

            var result = await _mediator.Send(new LoginCommand(Username, Password));
            if (!result.Success)
            {
                Error = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "login.error_sign_in_failed");
                return;
            }

            AuthCompleted?.Invoke(false);
        }
        catch (FluentValidation.ValidationException vex)
        {
            Error = UserFacingMessageLocalizer.LocalizeLinesOrFallback(
                vex.Errors.Select(static e => e.ErrorMessage),
                "login.error_sign_in_failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DoRegisterAsync()
    {
        try
        {
            IsBusy = true;
            Error = string.Empty;

            if (Password != ConfirmPassword)
            {
                Error = Loc.T("login.error_passwords_mismatch");
                return;
            }

            var result = await _mediator.Send(new RegisterCommand(Username, Email, Password));
            if (!result.Success)
            {
                Error = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "login.error_registration_failed");
                return;
            }

            AuthCompleted?.Invoke(true);
        }
        catch (FluentValidation.ValidationException vex)
        {
            Error = UserFacingMessageLocalizer.LocalizeLinesOrFallback(
                vex.Errors.Select(static e => e.ErrorMessage),
                "login.error_registration_failed");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
