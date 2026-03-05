using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Commands.Repository;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.SetupWizard;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.Views.Pages.SetupWizard;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class WelcomeWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _sp;
    private readonly INavigationService _nav;
    private readonly IWindowService _windows;
    private readonly IMediator _mediator;

    public WelcomeWindowViewModel(
        IServiceProvider sp,
        INavigationService nav,
        IWindowService windows,
        IMediator mediator)
    {
        _sp = sp;
        _nav = nav;
        _windows = windows;
        _mediator = mediator;
        NavigateToIntro();
    }

    [ObservableProperty] private object? _currentPage;
    public bool EnableTipsSelected { get; private set; }

    private void NavigateToIntro()
    {
        var vm = _sp.GetRequiredService<WelcomeIntroViewModel>();
        vm.StartRequested += NavigateToTips;
        CurrentPage = vm;
    }

    private void NavigateToTips()
    {
        var vm = _sp.GetRequiredService<WelcomeTipsOptInViewModel>();
        vm.ContinueRequested += OnContinueFromTips;
        CurrentPage = vm;
    }

    private void OnContinueFromTips(bool enableTips)
    {
        EnableTipsSelected = enableTips;
        NavigateToLogin();
    }

    private void NavigateToLogin()
    {
        var vm = _sp.GetRequiredService<LoginViewModel>();
        vm.LoginSucceeded += () => _ = RunSetupThenMainAsync();
        CurrentPage = vm;
    }

    private async Task RunSetupThenMainAsync()
    {
        var wizard = _windows.Create<SetupWizardWindow>();
        var wizardVm = _sp.GetRequiredService<SetupWizardViewModel>();
        wizard.DataContext = wizardVm;

        // Close wizard when VM requests it
        wizardVm.RequestClose += (_, _) => wizard.Close();

        Window? owner = _windows.GetActiveWindow();
        if (owner is not null)
            await _windows.ShowDialogAsync(wizard, owner);
        else
            _windows.Show(wizard);

        // After wizard: ensure repositories are created for all directories
        await _mediator.Send(new EnsureRepositoriesCommand());

        _nav.GoToMain();
    }
}
