using Microsoft.Data.Sqlite;

namespace Veyra.Infrastructure.Data;

public sealed class SqliteConnectionFactory(string connectionString) : ISqliteConnectionFactory
{
    public SqliteConnection CreateConnection() => new(connectionString);
}
