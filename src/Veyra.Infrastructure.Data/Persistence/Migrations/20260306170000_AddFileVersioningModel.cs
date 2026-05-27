using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260306170000_AddFileVersioningModel")]
public partial class AddFileVersioningModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FileIdentities",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileIdentities", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileIdentities_Repositories_RepositoryId",
                    column: x => x.RepositoryId,
                    principalTable: "Repositories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FileVersions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                FileIdentityId = table.Column<long>(type: "INTEGER", nullable: false),
                ContentHashSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsDeletionMarker = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileVersions", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileVersions_FileIdentities_FileIdentityId",
                    column: x => x.FileIdentityId,
                    principalTable: "FileIdentities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SnapshotFileLinks",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                FileIdentityId = table.Column<long>(type: "INTEGER", nullable: false),
                FileVersionId = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SnapshotFileLinks", x => x.Id);
                table.ForeignKey(
                    name: "FK_SnapshotFileLinks_FileIdentities_FileIdentityId",
                    column: x => x.FileIdentityId,
                    principalTable: "FileIdentities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_SnapshotFileLinks_FileVersions_FileVersionId",
                    column: x => x.FileVersionId,
                    principalTable: "FileVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_SnapshotFileLinks_RepositorySnapshots_SnapshotId",
                    column: x => x.SnapshotId,
                    principalTable: "RepositorySnapshots",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FileIdentities_RepositoryId_IsDeleted",
            table: "FileIdentities",
            columns: new[] { "RepositoryId", "IsDeleted" });

        migrationBuilder.CreateIndex(
            name: "IX_FileIdentities_RepositoryId_RelativePath",
            table: "FileIdentities",
            columns: new[] { "RepositoryId", "RelativePath" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FileVersions_ContentHashSha256",
            table: "FileVersions",
            column: "ContentHashSha256");

        migrationBuilder.CreateIndex(
            name: "IX_FileVersions_FileIdentityId_CreatedAt",
            table: "FileVersions",
            columns: new[] { "FileIdentityId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_SnapshotFileLinks_FileIdentityId_SnapshotId",
            table: "SnapshotFileLinks",
            columns: new[] { "FileIdentityId", "SnapshotId" });

        migrationBuilder.CreateIndex(
            name: "IX_SnapshotFileLinks_FileVersionId",
            table: "SnapshotFileLinks",
            column: "FileVersionId");

        migrationBuilder.CreateIndex(
            name: "IX_SnapshotFileLinks_SnapshotId_FileIdentityId",
            table: "SnapshotFileLinks",
            columns: new[] { "SnapshotId", "FileIdentityId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SnapshotFileLinks");
        migrationBuilder.DropTable(name: "FileVersions");
        migrationBuilder.DropTable(name: "FileIdentities");
    }
}
