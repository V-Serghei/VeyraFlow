using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class allchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CloudLastLocalSnapshotId",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CloudLastRemoteSnapshotId",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CloudLastSyncedAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloudSyncLastError",
                table: "Repositories",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloudSyncLastStatus",
                table: "Repositories",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncConflictStrategy",
                table: "Repositories",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "last_write_wins");

            migrationBuilder.AddColumn<int>(
                name: "SyncRetryBaseDelaySeconds",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "SyncRetryMaxAttempts",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.CreateTable(
                name: "RepositorySyncQueueItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteSnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConflictStrategy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 5),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedRemoteSnapshotId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySyncQueueItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositorySyncQueueItems_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_CloudLastSyncedAt_CloudLastRemoteSnapshotId",
                table: "Repositories",
                columns: new[] { "CloudLastSyncedAt", "CloudLastRemoteSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySyncQueueItems_RepositoryId_SnapshotId_OperationType",
                table: "RepositorySyncQueueItems",
                columns: new[] { "RepositoryId", "SnapshotId", "OperationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySyncQueueItems_RepositoryId_Status_NextAttemptAtUtc",
                table: "RepositorySyncQueueItems",
                columns: new[] { "RepositoryId", "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySyncQueueItems_Status_NextAttemptAtUtc_CreatedAt",
                table: "RepositorySyncQueueItems",
                columns: new[] { "Status", "NextAttemptAtUtc", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositorySyncQueueItems");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_CloudLastSyncedAt_CloudLastRemoteSnapshotId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CloudLastLocalSnapshotId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CloudLastRemoteSnapshotId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CloudLastSyncedAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CloudSyncLastError",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "CloudSyncLastStatus",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "SyncConflictStrategy",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "SyncRetryBaseDelaySeconds",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "SyncRetryMaxAttempts",
                table: "Repositories");
        }
    }
}
