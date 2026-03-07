using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Diffing;
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

        services.AddScoped<ITextDiffEngine>(sp =>
            new RustTextDiffEngine(
                sp.GetRequiredService<ManagedTextDiffEngine>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustTextDiffEngine>>()));

        services.AddScoped<ISnapshotComparisonEngine>(sp =>
            new RustSnapshotComparisonEngine(
                sp.GetRequiredService<ManagedSnapshotComparisonEngine>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustSnapshotComparisonEngine>>()));

        services.AddScoped<IRepositoryScanner, RustRepositoryScanner>();
        return services;
    }
}
