using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDataPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<VeyraDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.CommandTimeout(30);
                sqliteOptions.MigrationsAssembly(typeof(VeyraDbContext).Assembly.FullName);
            });
        });

        services.AddScoped<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(connectionString));
        return services;
    }
}
