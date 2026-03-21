using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Sync;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Services.Sync;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopInfrastructureOverrides(this IServiceCollection services)
    {
        services.AddScoped<IRepositoryCloudSyncOrchestrator, RepositoryCloudSyncOrchestrator>();
        services.AddSingleton<IRepositoryFsEventQueueService, RepositoryFsEventQueueService>();
        services.AddSingleton<INativeWordCompareService, NativeWordCompareService>();
        return services;
    }
}
