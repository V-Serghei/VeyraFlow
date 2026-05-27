using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Setup;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDataPersistence(this IServiceCollection services, string connectionString)
    {
        var runtimeConnectionString = BuildRuntimeConnectionString(connectionString);

        services.AddDbContext<VeyraDbContext>(options =>
        {
            options.UseSqlite(runtimeConnectionString, sqliteOptions =>
            {
                sqliteOptions.CommandTimeout(SqliteRuntimeDefaults.CommandTimeoutSeconds);
                sqliteOptions.MigrationsAssembly(typeof(VeyraDbContext).Assembly.FullName);
            });
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddScoped<ISqliteConnectionFactory>(_ => new SqliteConnectionFactory(runtimeConnectionString));
        services.AddScoped<IDatabaseStartupService, DatabaseStartupService>();
        return services;
    }

    private static string BuildRuntimeConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            DefaultTimeout = SqliteRuntimeDefaults.CommandTimeoutSeconds
        };

        return builder.ToString();
    }
}
