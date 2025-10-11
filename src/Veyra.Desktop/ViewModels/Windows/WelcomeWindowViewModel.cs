using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.SetupWizard;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.Views.Pages.SetupWizard;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class WelcomeWindowViewModel: ObservableObject
{
    private readonly IServiceProvider _sp;
    private readonly INavigationService _nav;
    private readonly IWindowService _windows;

    public WelcomeWindowViewModel(IServiceProvider sp, INavigationService nav, IWindowService windows)
    {
        _sp  = sp;
        _nav = nav;
        _windows = windows;
        NavigateToIntro();
    }
    [ObservableProperty] private object? currentPage;
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
        SetupWizardWindow wizard = _windows.Create<SetupWizardWindow>();
        var wizardVm = _sp.GetRequiredService<SetupWizardViewModel>();
        wizard.DataContext = wizardVm;

        Window? owner = _windows.GetActiveWindow();
        if (owner is not null)
            await _windows.ShowDialogAsync(wizard, owner);
        else
            _windows.Show(wizard);

        _nav.GoToMain();
    }

}
