using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260307193000_AddTextDiffHunks")]
public partial class AddTextDiffHunks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "HunkId",
            table: "FileVersionTextDiffLines",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "InHunkSequence",
            table: "FileVersionTextDiffLines",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "FileVersionTextDiffHunks",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiffId = table.Column<long>(type: "INTEGER", nullable: false),
                Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                OldStartLine = table.Column<int>(type: "INTEGER", nullable: false),
                OldLineCount = table.Column<int>(type: "INTEGER", nullable: false),
                NewStartLine = table.Column<int>(type: "INTEGER", nullable: false),
                NewLineCount = table.Column<int>(type: "INTEGER", nullable: false),
                ChangeKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileVersionTextDiffHunks", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileVersionTextDiffHunks_FileVersionTextDiffs_DiffId",
                    column: x => x.DiffId,
                    principalTable: "FileVersionTextDiffs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffLines_HunkId",
            table: "FileVersionTextDiffLines",
            column: "HunkId");

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffLines_HunkId_InHunkSequence",
            table: "FileVersionTextDiffLines",
            columns: new[] { "HunkId", "InHunkSequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffHunks_DiffId_Sequence",
            table: "FileVersionTextDiffHunks",
            columns: new[] { "DiffId", "Sequence" },
            unique: true);

        if (!ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.AddForeignKey(
                name: "FK_FileVersionTextDiffLines_FileVersionTextDiffHunks_HunkId",
                table: "FileVersionTextDiffLines",
                column: "HunkId",
                principalTable: "FileVersionTextDiffHunks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (!ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FileVersionTextDiffLines_FileVersionTextDiffHunks_HunkId",
                table: "FileVersionTextDiffLines");
        }

        migrationBuilder.DropTable(
            name: "FileVersionTextDiffHunks");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionTextDiffLines_HunkId",
            table: "FileVersionTextDiffLines");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionTextDiffLines_HunkId_InHunkSequence",
            table: "FileVersionTextDiffLines");

        migrationBuilder.DropColumn(
            name: "HunkId",
            table: "FileVersionTextDiffLines");

        migrationBuilder.DropColumn(
            name: "InHunkSequence",
            table: "FileVersionTextDiffLines");
    }
}

