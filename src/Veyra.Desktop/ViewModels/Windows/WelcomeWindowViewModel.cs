using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class WelcomeWindowViewModel: ObservableObject
{
    private readonly IServiceProvider _sp;
    private readonly INavigationService _nav;

    public WelcomeWindowViewModel(IServiceProvider sp, INavigationService nav)
    {
        _sp  = sp;
        _nav = nav;

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
        vm.LoginSucceeded += () => _nav.GoToMain();
        CurrentPage = vm;
    }

}
