using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchedSetupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchedDirectories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedDirectories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchedDirectoryFormats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DirectoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    FormatId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedDirectoryFormats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedDirectoryFormats_WatchedDirectories_DirectoryId",
                        column: x => x.DirectoryId,
                        principalTable: "WatchedDirectories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchedDirectoryFormats_WatchedFiles_FormatId",
                        column: x => x.FormatId,
                        principalTable: "WatchedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedDirectories_Path",
                table: "WatchedDirectories",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedDirectoryFormats_DirectoryId_FormatId",
                table: "WatchedDirectoryFormats",
                columns: new[] { "DirectoryId", "FormatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedDirectoryFormats_FormatId",
                table: "WatchedDirectoryFormats",
                column: "FormatId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedFiles_Pattern",
                table: "WatchedFiles",
                column: "Pattern",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchedDirectoryFormats");

            migrationBuilder.DropTable(
                name: "WatchedDirectories");

            migrationBuilder.DropTable(
                name: "WatchedFiles");
        }
    }
}
