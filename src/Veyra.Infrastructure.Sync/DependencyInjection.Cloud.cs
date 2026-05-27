using System;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Abstractions.Sync;
using Veyra.Infrastructure.Sync.Sync;

namespace Veyra.Infrastructure.Sync;

public static partial class DependencyInjection
{
    private static IServiceCollection AddSyncCloudServices(this IServiceCollection services, Uri baseAddress)
    {
        services.AddSingleton<ICloudMetadataProtectionService, NoopCloudMetadataProtectionService>();
        services.AddRetriedCloudHttpClient<ICloudSyncService, CloudSyncHttpService>(
            baseAddress,
            TimeSpan.FromSeconds(30),
            300);
        services.AddScoped<IRepositoryCloudSyncOrchestrator, NoopRepositoryCloudSyncOrchestrator>();
        return services;
    }
}
