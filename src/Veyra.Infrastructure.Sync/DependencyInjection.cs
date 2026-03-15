using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Abstractions.Sync;
using Veyra.Infrastructure.Sync.Auth;
using Veyra.Infrastructure.Sync.Sync;

namespace Veyra.Infrastructure.Sync;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureSync(
        this IServiceCollection services,
        IConfiguration config)
    {
        var baseUrl = config["CloudApi:BaseUrl"]
                      ?? Environment.GetEnvironmentVariable("VEYRA_CLOUDAPI_URL")
                      ?? "http://localhost:8080";

        services.AddSingleton<IAccessTokenPolicyService, AccessTokenPolicyService>();
        services.AddSingleton<ICloudMetadataProtectionService, NoopCloudMetadataProtectionService>();

        services.AddHttpClient<IAuthService, AuthHttpService>(c =>
            {
                c.BaseAddress = new Uri(baseUrl);
                c.Timeout = TimeSpan.FromSeconds(10);
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => (int)msg.StatusCode == 429)
                .WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(250 * i)));

        services.AddHttpClient<ICloudSyncService, CloudSyncHttpService>(c =>
            {
                c.BaseAddress = new Uri(baseUrl);
                c.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => (int)msg.StatusCode == 429)
                .WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(300 * i)));

        services.AddScoped<IRepositoryCloudSyncOrchestrator, NoopRepositoryCloudSyncOrchestrator>();

        return services;
    }
}
