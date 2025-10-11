using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Pages.WelcomeWindow;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;
using Veyra.Infrastructure.Data;
using Veyra.Desktop.ViewModels.Pages.AuthWindow;
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

        // Logging
        services.AddVeyraLogging();
        // Application & Infrastructure // Business Logic
        services.AddApplication();
        services.AddInfrastructureData(connectionString);
        services.AddInfrastructureSync(cfg);


        // Navigation & Window Services
        services.AddSingleton<IWindowService, WindowService >();
        services.AddSingleton<INavigationService, NavigationService>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<WelcomeIntroViewModel>();
        services.AddTransient<WelcomeTipsOptInViewModel>();
        // ViewModels & Views // DataContext bindings
        // Welcome Window
        services.AddTransient<WelcomeWindowViewModel>();
        services.AddTransient<WelcomeWindow>(sp =>
            new WelcomeWindow
            {
                DataContext = sp.GetRequiredService<WelcomeWindowViewModel>()
            });

        // Main Window
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>(sp =>
            new MainWindow
            {
                DataContext = sp.GetRequiredService<MainWindowViewModel>()
            });

        // Info Window
        services.AddTransient<InfoWindowViewModel>();
        services.AddTransient<InfoWindow>(sp =>
            new InfoWindow
            {
                DataContext = sp.GetRequiredService<InfoWindowViewModel>()
            });


        return services.BuildServiceProvider();


    }
}
