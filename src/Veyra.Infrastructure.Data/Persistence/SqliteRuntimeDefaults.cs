namespace Veyra.Infrastructure.Data.Persistence;

public static class SqliteRuntimeDefaults
{
    public const int CommandTimeoutSeconds = 120;
    public const int BusyTimeoutMilliseconds = CommandTimeoutSeconds * 1000;
}
