using System;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Maintenance;
using Veyra.Desktop.Services.Monitoring;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Services.Security;
using Veyra.Desktop.Services.Shell.Tray;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.Services.System;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAppTrayService, AppTrayService>();
        services.AddSingleton<IServiceScopeExecutor, ServiceScopeExecutor>();
        services.AddSingleton<IRetentionDefaultsStore, RetentionDefaultsStore>();
        services.AddSingleton<IAppTransientStateMaintenanceService, AppTransientStateMaintenanceService>();
        services.AddTransient<IRepositoryRetentionDefaultsApplier, RepositoryRetentionDefaultsApplier>();
        services.AddTransient<ISensitiveActionGuard, SensitiveActionGuard>();
        services.AddTransient<IAudioPreviewPlaybackService, AudioPreviewPlaybackService>();
        services.AddTransient<IOperationMonitorService, OperationMonitorService>();
        services.AddScoped<ILocalBlockStorageMetricsService, LocalBlockStorageMetricsService>();
        services.AddSingleton<IWindowsAutostartService>(_ =>
            OperatingSystem.IsWindows()
                ? new WindowsAutostartService()
                : new UnsupportedWindowsAutostartService());
        return services;
    }
}
