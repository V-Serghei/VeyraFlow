using Microsoft.Extensions.DependencyInjection;
using Veyra.Application;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.ViewModels.Windows;
using Veyra.Desktop.Views;
using Veyra.Desktop.Views.Windows;
using Veyra.Infrastructure.Data;

namespace Veyra.Desktop.CompositionRoot;

public static class DependencyInjection
{
    public static ServiceProvider BuildServiceProvider(string connectionString)
    {
        var services = new ServiceCollection();

        // Application & Infrastructure // Business Logic
        services.AddApplication();
        services.AddInfrastructureData(connectionString);

        // Navigation & Window Services
        services.AddSingleton<IWindowService, WindowService >();
        services.AddSingleton<INavigationService, NavigationService>();

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
