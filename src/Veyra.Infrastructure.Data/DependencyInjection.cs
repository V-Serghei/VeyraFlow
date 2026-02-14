using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Data.Persistence;
using Veyra.Infrastructure.Data.Setup;

namespace Veyra.Infrastructure.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<VeyraDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.CommandTimeout(30);
                sqliteOptions.MigrationsAssembly(typeof(VeyraDbContext).Assembly.FullName);
            });
        });

        services.AddScoped<ISqliteConnectionFactory, SqliteConnectionFactory>(sp => new SqliteConnectionFactory(connectionString));
        services.AddScoped<ISetupRepository, EfSetupRepository>();

        return services;
    }
}

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}

public class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
