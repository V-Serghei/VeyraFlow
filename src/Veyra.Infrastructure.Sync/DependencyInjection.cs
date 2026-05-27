using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Veyra.Infrastructure.Sync;

public static partial class DependencyInjection
{
    public static IServiceCollection AddInfrastructureSync(
        this IServiceCollection services,
        IConfiguration config)
    {
        Uri baseAddress = GetCloudApiBaseAddress(config);
        services.AddSyncAuthServices(baseAddress);
        services.AddSyncCloudServices(baseAddress);
        return services;
    }

    private static Uri GetCloudApiBaseAddress(IConfiguration config)
    {
        string baseUrl = config["CloudApi:BaseUrl"]
                         ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                         ?? "http://localhost:8080";
        return new Uri(baseUrl);
    }
}
