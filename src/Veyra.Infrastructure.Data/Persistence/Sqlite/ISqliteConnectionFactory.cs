using Microsoft.Data.Sqlite;

namespace Veyra.Infrastructure.Data;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}
