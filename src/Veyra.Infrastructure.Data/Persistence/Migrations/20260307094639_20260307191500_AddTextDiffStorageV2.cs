using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _20260307191500_AddTextDiffStorageV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TextLineAtoms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HashSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextLineAtoms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileVersionTextDiffLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiffId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LeftLineNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    RightLineNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    TextLineAtomId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileVersionTextDiffLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileVersionTextDiffLines_FileVersionTextDiffs_DiffId",
                        column: x => x.DiffId,
                        principalTable: "FileVersionTextDiffs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileVersionTextDiffLines_TextLineAtoms_TextLineAtomId",
                        column: x => x.TextLineAtomId,
                        principalTable: "TextLineAtoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionTextDiffLines_DiffId_Sequence",
                table: "FileVersionTextDiffLines",
                columns: new[] { "DiffId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionTextDiffLines_TextLineAtomId",
                table: "FileVersionTextDiffLines",
                column: "TextLineAtomId");

            migrationBuilder.CreateIndex(
                name: "IX_TextLineAtoms_HashSha256",
                table: "TextLineAtoms",
                column: "HashSha256");

            migrationBuilder.CreateIndex(
                name: "IX_TextLineAtoms_HashSha256_Text",
                table: "TextLineAtoms",
                columns: new[] { "HashSha256", "Text" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileVersionTextDiffLines");

            migrationBuilder.DropTable(
                name: "TextLineAtoms");
        }
    }
}
