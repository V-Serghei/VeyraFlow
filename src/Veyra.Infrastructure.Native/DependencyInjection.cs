using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    public static IServiceCollection AddInfrastructureNative(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddNativeCoreServices();
        services.AddNativeSecurityServices();
        services.AddNativeStorageServices();
        services.AddNativeDiffingServices();
        services.AddNativeScanningAndDiagnosticsServices();
        return services;
    }
}
