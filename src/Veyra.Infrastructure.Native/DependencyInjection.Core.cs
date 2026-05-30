using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Retention;
using Veyra.Infrastructure.Native.Setup;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    private static IServiceCollection AddNativeCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<INativeSetupApplier, NativeSetupApplier>();
        services.AddSingleton<INativeExecutionScheduler, NativeExecutionScheduler>();
        services.AddSingleton<INativeRuntimeHealthService, NativeRuntimeHealthService>();
        services.AddScoped<IRepositoryRetentionPlanner, RustRepositoryRetentionPlanner>();
        return services;
    }
}
