using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class allchange2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileVersionId",
                table: "SnapshotFileLinks",
                columns: new[] { "SnapshotId", "IsDeleted", "FileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySyncQueueItems_OperationType_Status_NextAttemptAtUtc_CreatedAt",
                table: "RepositorySyncQueueItems",
                columns: new[] { "OperationType", "Status", "NextAttemptAtUtc", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySyncQueueItems_OperationType_Status_UpdatedAt",
                table: "RepositorySyncQueueItems",
                columns: new[] { "OperationType", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_RepositoryId_SnapshotId_IsDeleted_RelativePath",
                table: "RepositorySnapshotEntries",
                columns: new[] { "RepositoryId", "SnapshotId", "IsDeleted", "RelativePath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileVersionId",
                table: "SnapshotFileLinks");

            migrationBuilder.DropIndex(
                name: "IX_RepositorySyncQueueItems_OperationType_Status_NextAttemptAtUtc_CreatedAt",
                table: "RepositorySyncQueueItems");

            migrationBuilder.DropIndex(
                name: "IX_RepositorySyncQueueItems_OperationType_Status_UpdatedAt",
                table: "RepositorySyncQueueItems");

            migrationBuilder.DropIndex(
                name: "IX_RepositorySnapshotEntries_RepositoryId_SnapshotId_IsDeleted_RelativePath",
                table: "RepositorySnapshotEntries");
        }
    }
}
