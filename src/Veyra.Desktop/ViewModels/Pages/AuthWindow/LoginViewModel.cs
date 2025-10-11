using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Veyra.Application.Commands.Auth;

namespace Veyra.Desktop.ViewModels.Pages.AuthWindow;

public partial class LoginViewModel : ObservableObject
{
    private readonly IMediator _mediator;

    public event System.Action? LoginSucceeded;

    [ObservableProperty] private string _username = "admin";
    [ObservableProperty] private string _password = "admin";
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public IAsyncRelayCommand LoginCommand { get; }

    public LoginViewModel(IMediator mediator)
    {
        _mediator = mediator;
        LoginCommand = new AsyncRelayCommand(DoLoginAsync);
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
                Error = result.Error ?? "Login failed";
                return;
            }

            LoginSucceeded?.Invoke();

        }
        catch (FluentValidation.ValidationException vex)
        {
            Error = string.Join("\n", vex.Errors.Select(e => e.ErrorMessage));
        }
        finally { IsBusy = false; }
    }
}
