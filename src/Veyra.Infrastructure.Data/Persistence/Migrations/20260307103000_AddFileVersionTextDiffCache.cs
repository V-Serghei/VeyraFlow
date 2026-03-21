using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260307103000_AddFileVersionTextDiffCache")]
public partial class AddFileVersionTextDiffCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FileVersionTextDiffs",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                LeftFileVersionId = table.Column<long>(type: "INTEGER", nullable: false),
                RightFileVersionId = table.Column<long>(type: "INTEGER", nullable: false),
                MaxLines = table.Column<int>(type: "INTEGER", nullable: false),
                DiffKeySha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                AddedLines = table.Column<int>(type: "INTEGER", nullable: false),
                RemovedLines = table.Column<int>(type: "INTEGER", nullable: false),
                IsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                LinesJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileVersionTextDiffs", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileVersionTextDiffs_FileVersions_LeftFileVersionId",
                    column: x => x.LeftFileVersionId,
                    principalTable: "FileVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_FileVersionTextDiffs_FileVersions_RightFileVersionId",
                    column: x => x.RightFileVersionId,
                    principalTable: "FileVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffs_DiffKeySha256",
            table: "FileVersionTextDiffs",
            column: "DiffKeySha256");

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_MaxLines",
            table: "FileVersionTextDiffs",
            columns: new[] { "LeftFileVersionId", "RightFileVersionId", "MaxLines" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffs_RightFileVersionId",
            table: "FileVersionTextDiffs",
            column: "RightFileVersionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FileVersionTextDiffs");
    }
}
