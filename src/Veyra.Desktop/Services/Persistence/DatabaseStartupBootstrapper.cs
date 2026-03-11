using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Veyra.Infrastructure.Data.Persistence;

namespace Veyra.Desktop.Services.Persistence;

internal static class DatabaseStartupBootstrapper
{
    private const string SnapshotTitleMigrationId = "20260306201000_AddRepositorySnapshotTitle";
    private const string SnapshotTitleEnsureMigrationId = "20260307130000_EnsureRepositorySnapshotTitleColumn";
    private const string TextDiffHunksMigrationId = "20260307193000_AddTextDiffHunks";
    private const string SoftDeleteCascadeMigrationId = "20260309133000_AddSoftDeleteCascadeModel";
    private const string RepositoryRetentionMigrationId = "20260309180000_AddRepositoryRetentionPolicy";
    private const string UserProfileSessionMigrationId = "20260309193000_AddUserProfileSessionColumns";
    private const string UserProfileEmailMigrationId = "20260311101500_AddUserProfileEmail";
    private const string OperationJournalMigrationId = "20260311103000_AddOperationJournalEntries";
    private const string UserProfileTokenLifecycleMigrationId = "20260311124500_AddUserProfileTokenLifecycle";
    private const string SyncUploadCheckpointMigrationId = "20260311141000_AddRepositorySyncUploadCheckpoint";
    private const string SensitiveActionVerificationMigrationId = "20260311193000_AddUserProfileSensitiveActionVerification";
    private const string EfProductVersion = "10.0.2";

    public static void Initialize(VeyraDbContext db)
    {
        BackfillSnapshotTitleMigrationHistoryIfNeeded(db);
        BackfillTextDiffHunksMigrationHistoryIfNeeded(db);
        BackfillSoftDeleteMigrationHistoryIfNeeded(db);
        BackfillRepositoryRetentionMigrationHistoryIfNeeded(db);
        BackfillUserProfileSessionMigrationHistoryIfNeeded(db);
        BackfillUserProfileEmailMigrationHistoryIfNeeded(db);
        BackfillOperationJournalMigrationHistoryIfNeeded(db);
        BackfillUserProfileTokenLifecycleMigrationHistoryIfNeeded(db);
        BackfillSyncUploadCheckpointMigrationHistoryIfNeeded(db);
        BackfillSensitiveActionVerificationMigrationHistoryIfNeeded(db);
        db.Database.Migrate();
        EnsureRepositorySnapshotTitleColumn(db);
        BackfillSnapshotTitleMigrationHistoryIfNeeded(db);
        BackfillTextDiffHunksMigrationHistoryIfNeeded(db);

        EnsureTextDiffStorageV2(db);
        EnsureSoftDeleteCascadeColumns(db);
        EnsureRepositoryRetentionColumns(db);
        EnsureUserProfileSessionColumns(db);
        EnsureRepositorySyncQueueCheckpointColumns(db);
        EnsureOperationJournalSchema(db);
        EnsureSensitiveActionVerificationColumn(db);
        BackfillTextDiffHunksMigrationHistoryIfNeeded(db);
        BackfillSoftDeleteMigrationHistoryIfNeeded(db);
        BackfillRepositoryRetentionMigrationHistoryIfNeeded(db);
        BackfillUserProfileSessionMigrationHistoryIfNeeded(db);
        BackfillUserProfileEmailMigrationHistoryIfNeeded(db);
        BackfillOperationJournalMigrationHistoryIfNeeded(db);
        BackfillUserProfileTokenLifecycleMigrationHistoryIfNeeded(db);
        BackfillSyncUploadCheckpointMigrationHistoryIfNeeded(db);
        BackfillSensitiveActionVerificationMigrationHistoryIfNeeded(db);
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
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
    private static void BackfillSoftDeleteMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("FileIdentities", "DeletedAt"),
                ("FileVersions", "IsDeleted"),
                ("FileVersions", "DeletedAt"),
                ("FileVersionBlocks", "IsDeleted"),
                ("FileVersionBlocks", "DeletedAt"),
                ("RepositorySnapshots", "IsDeleted"),
                ("RepositorySnapshots", "DeletedAt"),
                ("RepositorySnapshotEntries", "IsDeleted"),
                ("RepositorySnapshotEntries", "DeletedAt"),
                ("SnapshotFileLinks", "IsDeleted"),
                ("SnapshotFileLinks", "DeletedAt"),
                ("FileVersionTextDiffs", "IsDeleted"),
                ("FileVersionTextDiffs", "DeletedAt"),
                ("FileVersionTextDiffHunks", "IsDeleted"),
                ("FileVersionTextDiffHunks", "DeletedAt"),
                ("FileVersionTextDiffLines", "IsDeleted"),
                ("FileVersionTextDiffLines", "DeletedAt")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, SoftDeleteCascadeMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }
    private static void BackfillRepositoryRetentionMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("Repositories", "RetentionEnabled"),
                ("Repositories", "RetentionMaxAgeDays"),
                ("Repositories", "RetentionMaxSnapshots"),
                ("Repositories", "RetentionMaxTotalSizeBytes"),
                ("Repositories", "RetentionTriggerFilter"),
                ("Repositories", "RetentionRunIntervalMinutes"),
                ("Repositories", "RetentionLastRunAt"),
                ("Repositories", "RetentionLastStatus")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, RepositoryRetentionMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillUserProfileSessionMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("UserProfiles", "CloudUserId"),
                ("UserProfiles", "AccessToken")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, UserProfileSessionMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillUserProfileEmailMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            if (!SqliteHasColumn(connection, "UserProfiles", "Email"))
                return;

            EnsureMigrationHistoryRow(connection, UserProfileEmailMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillOperationJournalMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            if (!SqliteTableExists(connection, "OperationJournalEntries"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("OperationJournalEntries", "OccurredAtUtc"),
                ("OperationJournalEntries", "Level"),
                ("OperationJournalEntries", "Category"),
                ("OperationJournalEntries", "Action"),
                ("OperationJournalEntries", "Message")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, OperationJournalMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillUserProfileTokenLifecycleMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("UserProfiles", "CloudSessionId"),
                ("UserProfiles", "RefreshToken"),
                ("UserProfiles", "AccessTokenExpiresAtUtc"),
                ("UserProfiles", "RefreshTokenExpiresAtUtc")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, UserProfileTokenLifecycleMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillSyncUploadCheckpointMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            var requiredColumns = new (string Table, string Column)[]
            {
                ("RepositorySyncQueueItems", "UploadCheckpointNextIndex"),
                ("RepositorySyncQueueItems", "UploadCheckpointTotal"),
                ("RepositorySyncQueueItems", "UploadCheckpointSignature")
            };

            foreach (var (table, column) in requiredColumns)
            {
                if (!SqliteHasColumn(connection, table, column))
                    return;
            }

            EnsureMigrationHistoryRow(connection, SyncUploadCheckpointMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureUserProfileSessionColumns(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            EnsureSqliteColumnExists(connection, "UserProfiles", "CloudUserId", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "AccessToken", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "Email", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "CloudSessionId", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "RefreshToken", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "AccessTokenExpiresAtUtc", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "UserProfiles", "RefreshTokenExpiresAtUtc", "TEXT NULL");

            using var createIdx = connection.CreateCommand();
            createIdx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserProfiles_Email\" ON \"UserProfiles\" (\"Email\");";
            createIdx.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureOperationJournalSchema(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            using (var createTable = connection.CreateCommand())
            {
                createTable.CommandText = @"
CREATE TABLE IF NOT EXISTS ""OperationJournalEntries"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_OperationJournalEntries"" PRIMARY KEY AUTOINCREMENT,
    ""OccurredAtUtc"" TEXT NOT NULL,
    ""Level"" TEXT NOT NULL,
    ""Category"" TEXT NOT NULL,
    ""Action"" TEXT NOT NULL,
    ""RepositoryId"" INTEGER NULL,
    ""Username"" TEXT NULL,
    ""Message"" TEXT NOT NULL,
    ""Details"" TEXT NULL
);";
                createTable.ExecuteNonQuery();
            }

            using (var idx1 = connection.CreateCommand())
            {
                idx1.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_OperationJournalEntries_OccurredAtUtc_Id\" ON \"OperationJournalEntries\" (\"OccurredAtUtc\", \"Id\");";
                idx1.ExecuteNonQuery();
            }

            using (var idx2 = connection.CreateCommand())
            {
                idx2.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_OperationJournalEntries_Category_OccurredAtUtc\" ON \"OperationJournalEntries\" (\"Category\", \"OccurredAtUtc\");";
                idx2.ExecuteNonQuery();
            }

            using (var idx3 = connection.CreateCommand())
            {
                idx3.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_OperationJournalEntries_RepositoryId_OccurredAtUtc\" ON \"OperationJournalEntries\" (\"RepositoryId\", \"OccurredAtUtc\");";
                idx3.ExecuteNonQuery();
            }
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }
    private static void EnsureRepositoryRetentionColumns(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionEnabled", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionMaxAgeDays", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionMaxSnapshots", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionMaxTotalSizeBytes", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionTriggerFilter", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionRunIntervalMinutes", "INTEGER NOT NULL DEFAULT 60");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionLastRunAt", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "Repositories", "RetentionLastStatus", "TEXT NULL");

            using var createIdx = connection.CreateCommand();
            createIdx.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_Repositories_RetentionEnabled_RetentionLastRunAt\" ON \"Repositories\" (\"RetentionEnabled\", \"RetentionLastRunAt\");";
            createIdx.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureSoftDeleteCascadeColumns(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            EnsureSqliteColumnExists(connection, "FileIdentities", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "FileVersions", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersions", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "FileVersionBlocks", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionBlocks", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "RepositorySnapshots", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "RepositorySnapshots", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "RepositorySnapshotEntries", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "RepositorySnapshotEntries", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "SnapshotFileLinks", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "SnapshotFileLinks", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "FileVersionTextDiffs", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffs", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "FileVersionTextDiffHunks", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffHunks", "DeletedAt", "TEXT NULL");

            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "DeletedAt", "TEXT NULL");

            using (var createIdx1 = connection.CreateCommand())
            {
                createIdx1.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersions_FileIdentityId_IsDeleted_CreatedAt\" ON \"FileVersions\" (\"FileIdentityId\", \"IsDeleted\", \"CreatedAt\");";
                createIdx1.ExecuteNonQuery();
            }

            using (var createIdx2 = connection.CreateCommand())
            {
                createIdx2.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionBlocks_FileVersionId_IsDeleted_Sequence\" ON \"FileVersionBlocks\" (\"FileVersionId\", \"IsDeleted\", \"Sequence\");";
                createIdx2.ExecuteNonQuery();
            }

            using (var createIdx3 = connection.CreateCommand())
            {
                createIdx3.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_RepositorySnapshots_RepositoryId_IsDeleted_CreatedAt\" ON \"RepositorySnapshots\" (\"RepositoryId\", \"IsDeleted\", \"CreatedAt\");";
                createIdx3.ExecuteNonQuery();
            }

            using (var createIdx4 = connection.CreateCommand())
            {
                createIdx4.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_RepositorySnapshotEntries_SnapshotId_IsDeleted_RelativePath\" ON \"RepositorySnapshotEntries\" (\"SnapshotId\", \"IsDeleted\", \"RelativePath\");";
                createIdx4.ExecuteNonQuery();
            }

            using (var createIdx5 = connection.CreateCommand())
            {
                createIdx5.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileIdentityId\" ON \"SnapshotFileLinks\" (\"SnapshotId\", \"IsDeleted\", \"FileIdentityId\");";
                createIdx5.ExecuteNonQuery();
            }

            using (var createIdx6 = connection.CreateCommand())
            {
                createIdx6.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_IsDeleted_MaxLines\" ON \"FileVersionTextDiffs\" (\"LeftFileVersionId\", \"RightFileVersionId\", \"IsDeleted\", \"MaxLines\");";
                createIdx6.ExecuteNonQuery();
            }

            using (var createIdx7 = connection.CreateCommand())
            {
                createIdx7.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffHunks_DiffId_IsDeleted_Sequence\" ON \"FileVersionTextDiffHunks\" (\"DiffId\", \"IsDeleted\", \"Sequence\");";
                createIdx7.ExecuteNonQuery();
            }

            using (var createIdx8 = connection.CreateCommand())
            {
                createIdx8.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_FileVersionTextDiffLines_DiffId_IsDeleted_Sequence\" ON \"FileVersionTextDiffLines\" (\"DiffId\", \"IsDeleted\", \"Sequence\");";
                createIdx8.ExecuteNonQuery();
            }
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

            EnsureSqliteColumnExists(connection, "FileVersionTextDiffs", "StorageFormatVersion", "INTEGER NOT NULL DEFAULT 2");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffs", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffs", "DeletedAt", "TEXT NULL");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "HunkId", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffLines", "InHunkSequence", "INTEGER NULL");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffHunks", "StartLineSequence", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "FileVersionTextDiffHunks", "EndLineSequence", "INTEGER NOT NULL DEFAULT 0");

            using (var backfillRanges = connection.CreateCommand())
            {
                backfillRanges.CommandText = @"
UPDATE ""FileVersionTextDiffHunks""
SET ""StartLineSequence"" = COALESCE((
        SELECT MIN(l.""Sequence"")
        FROM ""FileVersionTextDiffLines"" AS l
        WHERE l.""HunkId"" = ""FileVersionTextDiffHunks"".""Id""), 0),
    ""EndLineSequence"" = COALESCE((
        SELECT MAX(l.""Sequence"")
        FROM ""FileVersionTextDiffLines"" AS l
        WHERE l.""HunkId"" = ""FileVersionTextDiffHunks"".""Id""), 0)
;";
                backfillRanges.ExecuteNonQuery();
            }

            using (var upgradeFormat = connection.CreateCommand())
            {
                upgradeFormat.CommandText = @"
UPDATE ""FileVersionTextDiffs""
SET ""StorageFormatVersion"" = 2
WHERE EXISTS (
      SELECT 1
      FROM ""FileVersionTextDiffLines"" AS l
      WHERE l.""DiffId"" = ""FileVersionTextDiffs"".""Id"")
  AND EXISTS (
      SELECT 1
      FROM ""FileVersionTextDiffHunks"" AS h
      WHERE h.""DiffId"" = ""FileVersionTextDiffs"".""Id"");";
                upgradeFormat.ExecuteNonQuery();
            }

            using (var invalidateLegacy = connection.CreateCommand())
            {
                invalidateLegacy.CommandText = @"
UPDATE ""FileVersionTextDiffs""
SET ""IsDeleted"" = 1,
    ""DeletedAt"" = COALESCE(""DeletedAt"", CURRENT_TIMESTAMP)
WHERE ""StorageFormatVersion"" < 2
  AND NOT (""IsDeleted"");";
                invalidateLegacy.ExecuteNonQuery();
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

    private static void EnsureRepositorySyncQueueCheckpointColumns(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            EnsureSqliteColumnExists(connection, "RepositorySyncQueueItems", "UploadCheckpointNextIndex", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "RepositorySyncQueueItems", "UploadCheckpointTotal", "INTEGER NOT NULL DEFAULT 0");
            EnsureSqliteColumnExists(connection, "RepositorySyncQueueItems", "UploadCheckpointSignature", "TEXT NULL");
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureSensitiveActionVerificationColumn(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            EnsureSqliteColumnExists(connection, "UserProfiles", "RequirePasswordForSensitiveActions", "INTEGER NOT NULL DEFAULT 0");
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void BackfillSensitiveActionVerificationMigrationHistoryIfNeeded(VeyraDbContext db)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            connection.Open();

        try
        {
            if (!SqliteTableExists(connection, "__EFMigrationsHistory"))
                return;

            if (!SqliteHasColumn(connection, "UserProfiles", "RequirePasswordForSensitiveActions"))
                return;

            EnsureMigrationHistoryRow(connection, SensitiveActionVerificationMigrationId);
        }
        finally
        {
            if (shouldClose)
                connection.Close();
        }
    }

    private static void EnsureSqliteColumnExists(IDbConnection connection, string tableName, string columnName, string definition)
    {
        if (!SqliteTableExists(connection, tableName))
            return;

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

    private static bool SqliteTableExists(IDbConnection connection, string tableName)
    {
        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\"='table' AND \"name\"=@name;";

        var nameParam = tableCheck.CreateParameter();
        nameParam.ParameterName = "@name";
        nameParam.Value = tableName;
        tableCheck.Parameters.Add(nameParam);

        return Convert.ToInt64(tableCheck.ExecuteScalar() ?? 0) > 0;
    }

    private static bool SqliteHasColumn(IDbConnection connection, string tableName, string columnName)
    {
        if (!SqliteTableExists(connection, tableName))
            return false;

        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var name = reader["name"]?.ToString();
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

}
