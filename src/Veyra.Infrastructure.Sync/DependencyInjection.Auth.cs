using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Veyra.Application.Abstractions.Auth;
using Veyra.Infrastructure.Sync.Auth;

namespace Veyra.Infrastructure.Sync;

public static partial class DependencyInjection
{
    private static IServiceCollection AddSyncAuthServices(this IServiceCollection services, Uri baseAddress)
    {
        services.AddSingleton<IAccessTokenPolicyService, AccessTokenPolicyService>();
        services.AddRetriedCloudHttpClient<IAuthService, AuthHttpService>(
            baseAddress,
            TimeSpan.FromSeconds(10),
            250);
        return services;
    }

    private static IHttpClientBuilder AddRetriedCloudHttpClient<TContract, TImplementation>(
        this IServiceCollection services,
        Uri baseAddress,
        TimeSpan timeout,
        int retryDelayStepMilliseconds)
        where TContract : class
        where TImplementation : class, TContract
    {
        return services.AddHttpClient<TContract, TImplementation>(client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = timeout;
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => (int)msg.StatusCode == 429)
                .WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(retryDelayStepMilliseconds * i)));
    }
}
