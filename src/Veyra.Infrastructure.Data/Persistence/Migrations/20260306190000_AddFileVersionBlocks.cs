using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260306190000_AddFileVersionBlocks")]
public partial class AddFileVersionBlocks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FileVersionBlocks",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                FileVersionId = table.Column<long>(type: "INTEGER", nullable: false),
                Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                BlockHashBlake3 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                LengthBytes = table.Column<int>(type: "INTEGER", nullable: false),
                StoredSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileVersionBlocks", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileVersionBlocks_FileVersions_FileVersionId",
                    column: x => x.FileVersionId,
                    principalTable: "FileVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionBlocks_BlockHashBlake3",
            table: "FileVersionBlocks",
            column: "BlockHashBlake3");

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionBlocks_FileVersionId_Sequence",
            table: "FileVersionBlocks",
            columns: new[] { "FileVersionId", "Sequence" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FileVersionBlocks");
    }
}
