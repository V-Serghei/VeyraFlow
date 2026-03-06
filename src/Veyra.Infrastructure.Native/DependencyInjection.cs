using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Native.Scanning;
using Veyra.Infrastructure.Native.Setup;
using Veyra.Infrastructure.Native.Storage;

namespace Veyra.Infrastructure.Native;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureNative(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<INativeSetupApplier, NativeSetupApplier>();
        services.AddSingleton<IFileContentStore>(sp =>
            new RustFileContentStore(
                configuration,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustFileContentStore>>()));
        services.AddScoped<IRepositoryScanner, RustRepositoryScanner>();
        return services;
    }
}

