using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class allchange1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenExpiresAtUtc",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CloudSessionId",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "UserProfiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshTokenExpiresAtUtc",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadCheckpointNextIndex",
                table: "RepositorySyncQueueItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UploadCheckpointSignature",
                table: "RepositorySyncQueueItems",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadCheckpointTotal",
                table: "RepositorySyncQueueItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "OperationJournalEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJournalEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_Email",
                table: "UserProfiles",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationJournalEntries_Category_OccurredAtUtc",
                table: "OperationJournalEntries",
                columns: new[] { "Category", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJournalEntries_OccurredAtUtc_Id",
                table: "OperationJournalEntries",
                columns: new[] { "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationJournalEntries_RepositoryId_OccurredAtUtc",
                table: "OperationJournalEntries",
                columns: new[] { "RepositoryId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationJournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_UserProfiles_Email",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AccessTokenExpiresAtUtc",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "CloudSessionId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "RefreshTokenExpiresAtUtc",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "UploadCheckpointNextIndex",
                table: "RepositorySyncQueueItems");

            migrationBuilder.DropColumn(
                name: "UploadCheckpointSignature",
                table: "RepositorySyncQueueItems");

            migrationBuilder.DropColumn(
                name: "UploadCheckpointTotal",
                table: "RepositorySyncQueueItems");
        }
    }
}
