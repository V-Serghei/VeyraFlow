using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    public static IServiceCollection AddInfrastructureData(this IServiceCollection services, string connectionString)
    {
        services.AddDataPersistence(connectionString);
        services.AddDataSetupServices();
        services.AddDataAuthServices();
        services.AddDataObservabilityServices();
        return services;
    }
}
