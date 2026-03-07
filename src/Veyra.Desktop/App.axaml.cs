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
            db.Database.Migrate();
            EnsureRepositorySnapshotTitleColumn(db);
            EnsureTextDiffStorageV2(db);
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

    private static void EnsureTextDiffStorageV2(VeyraDbContext db)
    {
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
                createDiffLines.CommandText = "CREATE TABLE IF NOT EXISTS \"FileVersionTextDiffLines\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_FileVersionTextDiffLines\" PRIMARY KEY AUTOINCREMENT, \"DiffId\" INTEGER NOT NULL, \"Sequence\" INTEGER NOT NULL, \"Kind\" TEXT NOT NULL, \"LeftLineNumber\" INTEGER NULL, \"RightLineNumber\" INTEGER NULL, \"TextLineAtomId\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL, CONSTRAINT \"FK_FileVersionTextDiffLines_FileVersionTextDiffs_DiffId\" FOREIGN KEY (\"DiffId\") REFERENCES \"FileVersionTextDiffs\" (\"Id\") ON DELETE CASCADE, CONSTRAINT \"FK_FileVersionTextDiffLines_TextLineAtoms_TextLineAtomId\" FOREIGN KEY (\"TextLineAtomId\") REFERENCES \"TextLineAtoms\" (\"Id\") ON DELETE CASCADE);";
                createDiffLines.ExecuteNonQuery();
            }

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
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
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

