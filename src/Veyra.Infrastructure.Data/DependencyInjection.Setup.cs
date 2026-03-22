using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Data.Setup;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDataSetupServices(this IServiceCollection services)
    {
        services.AddScoped<ISetupRepository, EfSetupRepository>();
        services.AddScoped<IRepositoryRepository, EfRepositoryRepository>();
        services.AddScoped<IRepositoryRetentionService, EfRepositoryRetentionService>();
        services.AddSingleton<IRepositorySnapshotArchiveService, RepositorySnapshotArchiveService>();
        services.AddScoped<IRepositoryIntegrityService, EfRepositoryIntegrityService>();
        services.AddScoped<IRepositoryBundleService, EfRepositoryBundleService>();
        services.AddScoped<IRepositoryRecoveryService, EfRepositoryRecoveryService>();
        services.AddScoped<IRepositorySnapshotRepository, EfRepositorySnapshotRepository>();
        return services;
    }
}
