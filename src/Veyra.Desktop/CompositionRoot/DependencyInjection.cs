using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.Explorer;
using Veyra.Desktop.ViewModels.Pages.RepositorySettings;
using Veyra.Desktop.ViewModels.Pages.SetupWizard;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Pages.SetupWizard;
using Veyra.Desktop.Views.Windows;
using Veyra.Infrastructure.Data;
using Veyra.Infrastructure.Native;
using Veyra.Infrastructure.Sync;
using Veyra.Shared.Logging;

namespace Veyra.Desktop.CompositionRoot;

public static class DependencyInjection
{
    public static ServiceProvider BuildServiceProvider(string connectionString)
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();

        services.AddVeyraLogging();

        services.AddApplication();
        services.AddInfrastructureData(connectionString);
        services.AddInfrastructureSync(cfg);
        services.AddInfrastructureNative(cfg);

        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddTransient<WelcomeWindowViewModel>();
        services.AddTransient<WelcomeIntroViewModel>();
        services.AddTransient<WelcomeTipsOptInViewModel>();
        services.AddTransient<LoginViewModel>();

        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SelectDirectoriesViewModel>();
        services.AddTransient<SelectFormatsViewModel>();

        services.AddTransient<RepositoryDashboardViewModel>();
        services.AddTransient<RepositoryExplorerViewModel>();
        services.AddTransient<RepositorySettingsViewModel>();

        services.AddTransient<CreateRepositoryWindowViewModel>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<InfoWindowViewModel>();

        services.AddTransient<WelcomeWindow>(sp =>
            new WelcomeWindow { DataContext = sp.GetRequiredService<WelcomeWindowViewModel>() });

        services.AddTransient<MainWindow>(sp =>
            new MainWindow { DataContext = sp.GetRequiredService<MainWindowViewModel>() });

        services.AddTransient<InfoWindow>(sp =>
            new InfoWindow { DataContext = sp.GetRequiredService<InfoWindowViewModel>() });

        services.AddTransient<CreateRepositoryWindow>(sp =>
            new CreateRepositoryWindow { DataContext = sp.GetRequiredService<CreateRepositoryWindowViewModel>() });

        services.AddTransient<SetupWizardWindow>();

        return services.BuildServiceProvider();
    }
}
