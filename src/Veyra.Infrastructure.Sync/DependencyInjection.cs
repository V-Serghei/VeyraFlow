using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Veyra.Application.Abstractions.Auth;
using Veyra.Infrastructure.Sync.Auth;

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

        services.AddHttpClient<IAuthService, AuthHttpService>(c =>
            {
                c.BaseAddress = new Uri(baseUrl);
                c.Timeout = TimeSpan.FromSeconds(5);
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => (int)msg.StatusCode == 429)
                .WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(200 * i)));

        return services;
    }
}
