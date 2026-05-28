using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.Services.Observability;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Desktop.Services.Scanning;
using Veyra.Desktop.Services.State;
using Veyra.Desktop.Services.Onboarding;
using Veyra.Domain.Observability;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopRuntimeServices(
        this IServiceCollection services,
        ISnapshotSchedulerSettingsStore schedulerSettingsStore,
        IRuntimeObservabilitySettingsStore runtimeObservabilitySettingsStore,
        RuntimeObservabilityControlService runtimeObservability,
        SnapshotSchedulerOptions schedulerOptions)
    {
        services.AddSingleton(schedulerOptions);
        services.AddSingleton(schedulerSettingsStore);
        services.AddSingleton(runtimeObservabilitySettingsStore);
        services.AddSingleton<IRuntimeObservabilityControlService>(runtimeObservability);
        services.AddSingleton<IRuntimeObservabilityState>(runtimeObservability);
        services.AddSingleton<IRepositoryScanStatusService, RepositoryScanStatusService>();
        services.AddSingleton<ISnapshotScheduler, SnapshotSchedulerService>();
        services.AddSingleton<IRepositoryDashboardFilterStore, RepositoryDashboardFilterStore>();
        services.AddSingleton<IRepositoryExplorerFilterStore, RepositoryExplorerFilterStore>();
        services.AddSingleton<IGlobalSearchFilterStore, GlobalSearchFilterStore>();
        services.AddSingleton<OnboardingStateService>();
        return services;
    }
}
