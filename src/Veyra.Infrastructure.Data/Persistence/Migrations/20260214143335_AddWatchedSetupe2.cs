using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchedSetupe2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchedDirectoryFormats_WatchedFiles_FormatId",
                table: "WatchedDirectoryFormats");

            migrationBuilder.DropPrimaryKey(
                name: "PK_WatchedFiles",
                table: "WatchedFiles");

            migrationBuilder.RenameTable(
                name: "WatchedFiles",
                newName: "D_WatchedFormats");

            migrationBuilder.RenameIndex(
                name: "IX_WatchedFiles_Pattern",
                table: "D_WatchedFormats",
                newName: "IX_D_WatchedFormats_Pattern");

            migrationBuilder.AddPrimaryKey(
                name: "PK_D_WatchedFormats",
                table: "D_WatchedFormats",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_WatchedDirectoryFormats_D_WatchedFormats_FormatId",
                table: "WatchedDirectoryFormats",
                column: "FormatId",
                principalTable: "D_WatchedFormats",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchedDirectoryFormats_D_WatchedFormats_FormatId",
                table: "WatchedDirectoryFormats");

            migrationBuilder.DropPrimaryKey(
                name: "PK_D_WatchedFormats",
                table: "D_WatchedFormats");

            migrationBuilder.RenameTable(
                name: "D_WatchedFormats",
                newName: "WatchedFiles");

            migrationBuilder.RenameIndex(
                name: "IX_D_WatchedFormats_Pattern",
                table: "WatchedFiles",
                newName: "IX_WatchedFiles_Pattern");

            migrationBuilder.AddPrimaryKey(
                name: "PK_WatchedFiles",
                table: "WatchedFiles",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_WatchedDirectoryFormats_WatchedFiles_FormatId",
                table: "WatchedDirectoryFormats",
                column: "FormatId",
                principalTable: "WatchedFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
