using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Data.Auth;
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

        services.AddScoped<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(connectionString));
        services.AddScoped<ISetupRepository, EfSetupRepository>();
        services.AddScoped<IRepositoryRepository, EfRepositoryRepository>();
        services.AddScoped<IRepositoryRetentionService, EfRepositoryRetentionService>();
        services.AddScoped<IRepositoryIntegrityService, EfRepositoryIntegrityService>();
        services.AddScoped<IRepositorySnapshotRepository, EfRepositorySnapshotRepository>();
        services.AddScoped<IUserProfileRepository, EfUserProfileRepository>();

        return services;
    }
}

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}

public sealed class SqliteConnectionFactory(string connectionString) : ISqliteConnectionFactory
{
    public SqliteConnection CreateConnection() => new(connectionString);
}
