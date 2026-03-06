using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Native.Scanning;
using Veyra.Infrastructure.Native.Setup;

namespace Veyra.Infrastructure.Native;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureNative(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<INativeSetupApplier, NativeSetupApplier>();
        services.AddScoped<IRepositoryScanner, RustRepositoryScanner>();
        return services;
    }
}
