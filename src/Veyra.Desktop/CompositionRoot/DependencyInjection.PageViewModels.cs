using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.Search;
using Veyra.Desktop.ViewModels.Pages.Settings;
using Veyra.Desktop.ViewModels.Pages.SetupWizard;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopPageViewModels(this IServiceCollection services)
    {
        services.AddTransient<WelcomeIntroViewModel>();
        services.AddTransient<WelcomeTipsOptInViewModel>();
        services.AddTransient<LoginViewModel>();

        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SelectDirectoriesViewModel>();
        services.AddTransient<SelectFormatsViewModel>();

        services.AddTransient<RepositoryDashboardViewModel>();
        services.AddTransient<RepositoryExplorerViewModel>();
        services.AddTransient<RepositorySettingsViewModel>();
        services.AddTransient<GlobalSearchViewModel>();
        services.AddTransient<AppSettingsViewModel>();
        return services;
    }
}
