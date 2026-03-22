using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Sync;
using Veyra.Application.Queries;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Desktop.Styling;
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
    private readonly IRepositoryCloudSyncOrchestrator _cloudSync;
    private readonly OnboardingStateService _onboardingState;
    private readonly ILogger<WelcomeWindowViewModel> _log;
    private readonly UserExperienceManager _experience = UserExperienceManager.Instance;

    public WelcomeWindowViewModel(
        IServiceProvider sp,
        INavigationService nav,
        IWindowService windows,
        IMediator mediator,
        IRepositoryCloudSyncOrchestrator cloudSync,
        OnboardingStateService onboardingState,
        ILogger<WelcomeWindowViewModel> log)
    {
        _sp = sp;
        _nav = nav;
        _windows = windows;
        _mediator = mediator;
        _cloudSync = cloudSync;
        _onboardingState = onboardingState;
        _log = log;
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

    private void OnContinueFromTips(WelcomeTipsSelection selection)
    {
        EnableTipsSelected = selection.EnableTips;
        _experience.SetMode(selection.ExperienceModeCode);

        if (selection.EnableTips)
            _onboardingState.RequestFirstRunTour();

        NavigateToLogin();
    }

    private void NavigateToLogin()
    {
        var vm = _sp.GetRequiredService<LoginViewModel>();
        vm.AuthCompleted += isNewUser => _ = ExecuteNavigationAsync(() => ContinueAfterAuthAsync(isNewUser));
        vm.GuestModeRequested += () => _ = ExecuteNavigationAsync(ContinueAsGuestAsync);
        CurrentPage = vm;
    }

    private async Task ContinueAfterAuthAsync(bool isNewUser)
    {
        if (isNewUser)
        {
            await RunSetupThenMainAsync();
            return;
        }

        var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
        if (repositories.Count > 0)
        {
            _nav.GoToMain();
            return;
        }

        try
        {
            await _cloudSync.RestoreRepositoriesFromCloudAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloud restore after auth is unavailable. Falling back to local setup.");
        }

        repositories = await _mediator.Send(new GetAllRepositoriesQuery());
        if (repositories.Count > 0)
        {
            _nav.GoToMain();
            return;
        }

        await RunSetupThenMainAsync();
    }

    private async Task ContinueAsGuestAsync()
    {
        var repositories = await _mediator.Send(new GetAllRepositoriesQuery());
        if (repositories.Count > 0)
        {
            _nav.GoToMain();
            return;
        }

        await RunSetupThenMainAsync();
    }

    private async Task RunSetupThenMainAsync()
    {
        var wizard = _windows.Create<SetupWizardWindow>();
        var wizardVm = _sp.GetRequiredService<SetupWizardViewModel>();
        wizard.DataContext = wizardVm;

        wizardVm.RequestClose += (_, _) => wizard.Close();

        Window? owner = _windows.GetActiveWindow();
        if (owner is not null)
            await _windows.ShowDialogAsync(wizard, owner);
        else
            _windows.Show(wizard);

        _nav.GoToMain();
    }

    private async Task ExecuteNavigationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Welcome flow navigation failed");
            if (CurrentPage is LoginViewModel loginVm)
                loginVm.Error = UserFacingMessageLocalizer.LocalizeExceptionOrFallback(ex, "login.error_sign_in_failed");
        }
    }
}
