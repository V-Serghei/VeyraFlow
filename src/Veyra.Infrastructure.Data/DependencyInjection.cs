using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Infrastructure.Data.Persistence;

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
            });
        });

        services.AddScoped<ISqliteConnectionFactory, SqliteConnectionFactory>(sp =>
        {
            return new SqliteConnectionFactory(connectionString);
        });

        return services;
    }

    public static void EnableWalMode(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
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
