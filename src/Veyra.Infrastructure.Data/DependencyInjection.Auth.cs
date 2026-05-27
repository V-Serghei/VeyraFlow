using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Auth;
using Veyra.Infrastructure.Data.Auth;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDataAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IUserProfileRepository, EfUserProfileRepository>();
        return services;
    }
}
