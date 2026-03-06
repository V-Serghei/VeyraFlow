using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FileCount",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScannedAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalSizeBytes",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "VersionCount",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RepositorySnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TotalEntries = table.Column<int>(type: "INTEGER", nullable: false),
                    FileEntries = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryEntries = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalFileBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositorySnapshots_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepositorySnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ParentRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentHashSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositorySnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositorySnapshotEntries_RepositorySnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "RepositorySnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_ContentHashSha256",
                table: "RepositorySnapshotEntries",
                column: "ContentHashSha256");

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_RepositoryId_RelativePath",
                table: "RepositorySnapshotEntries",
                columns: new[] { "RepositoryId", "RelativePath" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_SnapshotId_ParentRelativePath",
                table: "RepositorySnapshotEntries",
                columns: new[] { "SnapshotId", "ParentRelativePath" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_SnapshotId_RelativePath",
                table: "RepositorySnapshotEntries",
                columns: new[] { "SnapshotId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshots_RepositoryId_CreatedAt",
                table: "RepositorySnapshots",
                columns: new[] { "RepositoryId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositorySnapshotEntries");

            migrationBuilder.DropTable(
                name: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "FileCount",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "LastScannedAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "TotalSizeBytes",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "VersionCount",
                table: "Repositories");
        }
    }
}
