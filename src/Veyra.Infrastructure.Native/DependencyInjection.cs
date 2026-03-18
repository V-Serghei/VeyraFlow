using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Observability;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Diagnostics;
using Veyra.Infrastructure.Native.Diffing;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Scanning;
using Veyra.Infrastructure.Native.Security;
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
        services.AddSingleton<INativeExecutionScheduler, NativeExecutionScheduler>();

        services.AddSingleton<ArtifactMasterKeyStore>();
        services.AddSingleton<ArtifactKeyManagementService>();
        services.AddSingleton<IArtifactKeyManagementService>(sp => sp.GetRequiredService<ArtifactKeyManagementService>());
        services.AddSingleton<ArtifactBlockCryptor>();
        services.AddSingleton<ICloudMetadataProtectionService, CloudMetadataProtectionService>();

        services.AddSingleton<IFileContentStore, RustFileContentStore>();

        services.AddScoped<ITextDiffEngine>(sp =>
            new RustTextDiffEngine(
                sp.GetRequiredService<ManagedTextDiffEngine>(),
                sp.GetRequiredService<INativeExecutionScheduler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustTextDiffEngine>>()));

        services.AddScoped<ISnapshotComparisonEngine>(sp =>
            new RustSnapshotComparisonEngine(
                sp.GetRequiredService<ManagedSnapshotComparisonEngine>(),
                sp.GetRequiredService<INativeExecutionScheduler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustSnapshotComparisonEngine>>()));

        services.AddScoped<IRepositoryScanner, RustRepositoryScanner>();
        services.AddScoped<IAppDiagnosticsService, AppDiagnosticsService>();
        return services;
    }
}
