using System;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Abstractions.Sync;
using Microsoft.Extensions.Configuration;

namespace Veyra.Infrastructure.Sync;

public static partial class DependencyInjection
{
    public static IServiceCollection AddInfrastructureSync(
        this IServiceCollection services,
        IConfiguration config)
    {
        var baseAddress = GetCloudApiBaseAddress(config);
        services.AddSyncAuthServices(baseAddress);
        services.AddSyncCloudServices(baseAddress);
        return services;
    }

    private static Uri GetCloudApiBaseAddress(IConfiguration config)
    {
        var baseUrl = config["CloudApi:BaseUrl"]
                      ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                      ?? "http://localhost:8080";
        return new Uri(baseUrl);
    }
}
