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

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRegisterMode;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand RegisterCommand { get; }
    public IRelayCommand ToggleModeCommand { get; }

    public string ActionTitle => IsRegisterMode ? Loc.T("login.action_title_register") : Loc.T("login.action_title_sign_in");
    public string SubmitLabel => IsRegisterMode ? Loc.T("auth.register") : Loc.T("auth.sign_in");
    public string ToggleLabel => IsRegisterMode
        ? Loc.T("login.toggle_to_sign_in")
        : Loc.T("login.toggle_to_register");

    public LoginViewModel(IMediator mediator)
    {
        _mediator = mediator;
        LoginCommand = new AsyncRelayCommand(DoLoginAsync);
        RegisterCommand = new AsyncRelayCommand(DoRegisterAsync);
        ToggleModeCommand = new RelayCommand(ToggleMode);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActionTitle));
            OnPropertyChanged(nameof(SubmitLabel));
            OnPropertyChanged(nameof(ToggleLabel));
        };
    }

    partial void OnIsRegisterModeChanged(bool value)
    {
        Error = string.Empty;
        OnPropertyChanged(nameof(ActionTitle));
        OnPropertyChanged(nameof(SubmitLabel));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
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
                Error = result.Error ?? Loc.T("login.error_sign_in_failed");
                return;
            }

            AuthCompleted?.Invoke(false);
        }
        catch (FluentValidation.ValidationException vex)
        {
            Error = string.Join("\n", vex.Errors.Select(e => e.ErrorMessage));
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

            var result = await _mediator.Send(new RegisterCommand(Username, Password));
            if (!result.Success)
            {
                Error = result.Error ?? Loc.T("login.error_registration_failed");
                return;
            }

            AuthCompleted?.Invoke(true);
        }
        catch (FluentValidation.ValidationException vex)
        {
            Error = string.Join("\n", vex.Errors.Select(e => e.ErrorMessage));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
