using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

public partial class AddOperationJournalEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OperationJournalEntries");
    }
}
