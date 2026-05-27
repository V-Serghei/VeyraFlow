using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Observability;
using Veyra.Infrastructure.Native.Diagnostics;
using Veyra.Infrastructure.Native.Scanning;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    private static IServiceCollection AddNativeScanningAndDiagnosticsServices(this IServiceCollection services)
    {
        services.AddScoped<IRepositoryScanner, RustRepositoryScanner>();
        services.AddScoped<IAppDiagnosticsService, AppDiagnosticsService>();
        return services;
    }
}
