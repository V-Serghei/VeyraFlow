using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Data;
using System.IO;
using Avalonia.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Abstractions.Setup;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Scheduling;
using Veyra.Infrastructure.Data.Persistence;
using AvaloniaApplication = Avalonia.Application;
using DependencyInjection = Veyra.Desktop.CompositionRoot.DependencyInjection;

namespace Veyra.Desktop;

public partial class App : AvaloniaApplication
{
    public static IServiceProvider? _serviceProvider { get; private set; } = null!;
    private static ISnapshotScheduler? _snapshotScheduler;

    private const string SnapshotTitleMigrationId = "20260306201000_AddRepositorySnapshotTitle";
    private const string SnapshotTitleEnsureMigrationId = "20260307130000_EnsureRepositorySnapshotTitleColumn";
    private const string TextDiffHunksMigrationId = "20260307193000_AddTextDiffHunks";
    private const string EfProductVersion = "10.0.2";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow", "veyra.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connectionString = $"Data Source={dbPath}";

        _serviceProvider = DependencyInjection.BuildServiceProvider(connectionString);

        var shouldOpenMain = false;

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VeyraDbContext>();

            BackfillSnapshotTitleMigrationHistoryIfNeeded(db);
            BackfillTextDiffHunksMigrationHistoryIfNeeded(db);
            db.Database.Migrate();
            EnsureRepositorySnapshotTitleColumn(db);
            BackfillSnapshotTitleMigrationHistoryIfNeeded(db);
            BackfillTextDiffHunksMigrationHistoryIfNeeded(db);

            EnsureTextDiffStorageV2(db);
            BackfillTextDiffHunksMigrationHistoryIfNeeded(db);
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            var userProfiles = scope.ServiceProvider.GetRequiredService<IUserProfileRepository>();
            var setup = scope.ServiceProvider.GetRequiredService<ISetupRepository>();

            var activeUsername = userProfiles.GetActiveUsernameAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(activeUsername))
            {
                var dirs = setup.GetWatchedDirectoriesAsync().GetAwaiter().GetResult();
                var exts = setup.GetTrackedExtensionsAsync().GetAwaiter().GetResult();
                shouldOpenMain = dirs.Count > 0 && exts.Count > 0;
            }
        }

        _snapshotScheduler = _serviceProvider.GetService<ISnapshotScheduler>();
        _snapshotScheduler?.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktop)
        {
            classicDesktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            classicDesktop.Exit += OnDesktopExit;

            var nav = _serviceProvider.GetRequiredService<INavigationService>();

            if (shouldOpenMain)
                nav.GoToMain();
            else
                nav.ShowWelcome();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void EnsureRepositorySnapshotTitleColumn(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            var hasTitle = false;

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA table_info(\"RepositorySnapshots\");";
                using var reader = check.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader["name"]?.ToString();
                    if (string.Equals(name, "Title", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTitle = true;
                        break;
                    }
                }
            }

            if (hasTitle)
                return;

            using (var alter = connection.CreateCommand())
            {
                alter.CommandText = "ALTER TABLE \"RepositorySnapshots\" ADD COLUMN \"Title\" TEXT NULL;";
                alter.ExecuteNonQuery();
            }

            Log.Warning("Database schema repair applied: added missing RepositorySnapshots.Title column.");
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillSnapshotTitleMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            var hasSnapshotsTable = false;
            using (var tableCheck = connection.CreateCommand())
            {
                tableCheck.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"='RepositorySnapshots';";
                var count = Convert.ToInt64(tableCheck.ExecuteScalar() ?? 0);
                hasSnapshotsTable = count > 0;
            }

            if (!hasSnapshotsTable)
                return;

            var hasTitle = false;
            using (var titleCheck = connection.CreateCommand())
            {
                titleCheck.CommandText = "PRAGMA table_info(\"RepositorySnapshots\");";
                using var reader = titleCheck.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader["name"]?.ToString();
                    if (string.Equals(name, "Title", StringComparison.OrdinalIgnoreCase))
                    {
                        hasTitle = true;
                        break;
                    }
                }
            }

            if (!hasTitle)
                return;

            var hasHistoryTable = false;
            using (var historyCheck = connection.CreateCommand())
            {
                historyCheck.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"='__EFMigrationsHistory';";
                var count = Convert.ToInt64(historyCheck.ExecuteScalar() ?? 0);
                hasHistoryTable = count > 0;
            }

            if (!hasHistoryTable)
                return;

            EnsureMigrationHistoryRow(connection, SnapshotTitleMigrationId);
            EnsureMigrationHistoryRow(connection, SnapshotTitleEnsureMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureMigrationHistoryRow(IDbConnection connection, string migrationId)
    {
        using var existsCmd = connection.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = @id;";

        var existsParam = existsCmd.CreateParameter();
        existsParam.ParameterName = "@id";
        existsParam.Value = migrationId;
        existsCmd.Parameters.Add(existsParam);

        var existsCount = Convert.ToInt64(existsCmd.ExecuteScalar() ?? 0);
        if (existsCount > 0)
            return;

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @ver);";

        var idParam = insertCmd.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = migrationId;
        insertCmd.Parameters.Add(idParam);

        var verParam = insertCmd.CreateParameter();
        verParam.ParameterName = "@ver";
        verParam.Value = EfProductVersion;
        insertCmd.Parameters.Add(verParam);

        insertCmd.ExecuteNonQuery();

        Log.Warning("Database migration history backfill applied for {MigrationId}.", migrationId);
    }

    private static void BackfillTextDiffHunksMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            var hasHistoryTable = false;
            using (var historyCheck = connection.CreateCommand())
            {
                historyCheck.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"='__EFMigrationsHistory';";
                hasHistoryTable = Convert.ToInt64(historyCheck.ExecuteScalar() ?? 0) > 0;
            }

            if (!hasHistoryTable)
                return;

            var hasHunksTable = false;
            using (var hunksCheck = connection.CreateCommand())
            {
                hunksCheck.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"='FileVersionTextDiffHunks';";
                hasHunksTable = Convert.ToInt64(hunksCheck.ExecuteScalar() ?? 0) > 0;
            }

            if (!hasHunksTable)
                return;

            var hasHunkId = false;
            var hasInHunkSequence = false;
            using (var linesCheck = connection.CreateCommand())
            {
                linesCheck.CommandText = "PRAGMA table_info(\"FileVersionTextDiffLines\");";
                using var reader = linesCheck.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader["name"]?.ToString();
                    if (string.Equals(name, "HunkId", StringComparison.OrdinalIgnoreCase))
                        hasHunkId = true;
                    if (string.Equals(name, "InHunkSequence", StringComparison.OrdinalIgnoreCase))
                        hasInHunkSequence = true;
                }
            }

            if (!hasHunkId || !hasInHunkSequence)
                return;

            EnsureMigrationHistoryRow(connection, TextDiffHunksMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }
    private static void EnsureTextDiffStorageV2(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            using (var createAtoms = connection.CreateCommand())
            {
                createAtoms.CommandText = "CREATE TABLE IF NOT EXISTS \"TextLineAtoms\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_TextLineAtoms\" PRIMARY KEY AUTOINCREMENT, \"HashSha256\" TEXT NOT NULL, \"Text\" TEXT NOT NULL, \"CreatedAt\" TEXT NOT NULL);";
                createAtoms.ExecuteNonQuery();
            }

            using (var createDiffLines = connection.CreateCommand())
            {
                createDiffLines.CommandText = "CREATE TABLE IF NOT EXISTS \"FileVersionTextDiffLines\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_FileVersionTextDiffLines\" PRIMARY KEY AUTOINCREMENT, \"DiffId\" INTEGER NOT NULL, \"Sequence\" INTEGER NOT NULL, \"Kind\" TEXT NOT NULL, \"LeftLineNumber\" INTEGER NULL, \"RightLineNumber\" INTEGER NULL, \"HunkId\" INTEGER NULL, \"InHunkSequence\" INTEGER NULL, \"TextLineAtomId\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL, CONSTRAINT \"FK_FileVersionTextDiffLines_FileVersionTextDiffs_DiffId\" FOREIGN KEY (\"DiffId\") REFERENCES \"FileVersionTextDiffs\" (\"Id\") ON DELETE CASCADE, CONSTRAINT \"FK_FileVersionTextDiffLines_TextLineAtoms_TextLineAtomId\" FOREIGN KEY (\"TextLineAtomId\") REFERENCES \"TextLineAtoms\" (\"Id\") ON DELETE CASCADE);";
                createDiffLines.ExecuteNonQuery();
            }
            using (var createHunks = connection.CreateCommand())
            {
                createHunks.CommandText = "CREATE TABLE IF NOT EXISTS \"FileVersionTextDiffHunks\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_FileVersionTextDiffHunks\" PRIMARY KEY AUTOINCREMENT, \"DiffId\" INTEGER NOT NULL, \"Sequence\" INTEGER NOT NULL, \"OldStartLine\" INTEGER NOT NULL, \"OldLineCount\" INTEGER NOT NULL, \"NewStartLine\" INTEGER NOT NULL, \"NewLineCount\" INTEGER NOT NULL, \"ChangeKind\" TEXT NOT NULL, \"CreatedAt\" TEXT NOT NULL, CONSTRAINT \"FK_FileVersionTextDiffHunks_FileVersionTextDiffs_DiffId\" FOREIGN KEY (\"DiffId\") REFERENCES \"FileVersionTextDiffs\" (\"Id\") ON DELETE CASCADE);";
                createHunks.ExecuteNonQuery();
            }

            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "HunkId", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "InHunkSequence", "INTEGER NULL");

            using (var createIdx1 = connection.CreateCommand())
            {
                createIdx1.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TextLineAtoms_HashSha256_Text\" ON \"TextLineAtoms\" (\"HashSha256\", \"Text\");";
                createIdx1.ExecuteNonQuery();
            }

            using (var createIdx2 = connection.CreateCommand())
            {
                createIdx2.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_TextLineAtoms_HashSha256\" ON \"TextLineAtoms\" (\"HashSha256\");";
                createIdx2.ExecuteNonQuery();
            }

            using (var createIdx3 = connection.CreateCommand())
            {
                createIdx3.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffLines_DiffId_Sequence\" ON \"FileVersionTextDiffLines\" (\"DiffId\", \"Sequence\");";
                createIdx3.ExecuteNonQuery();
            }

            using (var createIdx4 = connection.CreateCommand())
            {
                createIdx4.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffLines_TextLineAtomId\" ON \"FileVersionTextDiffLines\" (\"TextLineAtomId\");";
                createIdx4.ExecuteNonQuery();
            }

            using (var createIdx5 = connection.CreateCommand())
            {
                createIdx5.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffHunks_DiffId\" ON \"FileVersionTextDiffHunks\" (\"DiffId\");";
                createIdx5.ExecuteNonQuery();
            }

            using (var createIdx6 = connection.CreateCommand())
            {
                createIdx6.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffHunks_DiffId_Sequence\" ON \"FileVersionTextDiffHunks\" (\"DiffId\", \"Sequence\");";
                createIdx6.ExecuteNonQuery();
            }

            using (var createIdx7 = connection.CreateCommand())
            {
                createIdx7.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffLines_HunkId\" ON \"FileVersionTextDiffLines\" (\"HunkId\");";
                createIdx7.ExecuteNonQuery();
            }

            using (var createIdx8 = connection.CreateCommand())
            {
                createIdx8.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffLines_HunkId_InHunkSequence\" ON \"FileVersionTextDiffLines\" (\"HunkId\", \"InHunkSequence\");";
                createIdx8.ExecuteNonQuery();
            }
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureSqliteColumnExists(IDbConnection connection, string tableName, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        var hasColumn = false;
        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader["name"]?.ToString();
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {definition};";
        alter.ExecuteNonQuery();

        Log.Warning("Database schema repair applied: added missing column {Table}.{Column}.", tableName, columnName);
    }
    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _snapshotScheduler?.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        Log.CloseAndFlush();
    }
}


