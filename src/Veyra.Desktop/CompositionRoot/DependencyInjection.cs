using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.Dashboard;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
using Veyra.Desktop.ViewModels.Pages.SetupWizard;
using Veyra.Desktop.Views.Pages.SetupWizard;
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

        // ── Cross-cutting ──────────────────────────────────────
        services.AddVeyraLogging();

        // ── Application & Infrastructure ───────────────────────
        services.AddApplication();
        services.AddInfrastructureData(connectionString);
        services.AddInfrastructureSync(cfg);
        services.AddInfrastructureNative(cfg);

        // ── Navigation & Window Services ───────────────────────
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INavigationService, NavigationService>();

        // ── ViewModels ─────────────────────────────────────────
        // Welcome flow
        services.AddTransient<WelcomeWindowViewModel>();
        services.AddTransient<WelcomeIntroViewModel>();
        services.AddTransient<WelcomeTipsOptInViewModel>();
        services.AddTransient<LoginViewModel>();

        // Setup wizard
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SelectDirectoriesViewModel>();
        services.AddTransient<SelectFormatsViewModel>();

        // Dashboard
        services.AddTransient<RepositoryDashboardViewModel>();

        // Main / Info
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<InfoWindowViewModel>();

        // ── Windows ────────────────────────────────────────────
        services.AddTransient<WelcomeWindow>(sp =>
            new WelcomeWindow { DataContext = sp.GetRequiredService<WelcomeWindowViewModel>() });

        services.AddTransient<MainWindow>(sp =>
            new MainWindow { DataContext = sp.GetRequiredService<MainWindowViewModel>() });

        services.AddTransient<InfoWindow>(sp =>
            new InfoWindow { DataContext = sp.GetRequiredService<InfoWindowViewModel>() });

        services.AddTransient<SetupWizardWindow>();

        return services.BuildServiceProvider();
    }
}
