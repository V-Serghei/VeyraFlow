using Microsoft.Extensions.DependencyInjection;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Services.Repositories;
using Veyra.Desktop.Services.Sync;

namespace Veyra.Desktop.CompositionRoot;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDesktopInfrastructureOverrides(this IServiceCollection services)
    {
        services.AddSingleton<IRepositoryFsEventQueueService, RepositoryFsEventQueueService>();
        services.AddSingleton<INativeWordCompareService, NativeWordCompareService>();
        return services;
    }
}
