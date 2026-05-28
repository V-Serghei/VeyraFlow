using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260528010000_AddRepositoryCloudRepositoryId")]
public partial class AddRepositoryCloudRepositoryId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CloudRepositoryId",
            table: "Repositories",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Repositories_CloudRepositoryId",
            table: "Repositories",
            column: "CloudRepositoryId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Repositories_CloudRepositoryId",
            table: "Repositories");

        migrationBuilder.DropColumn(
            name: "CloudRepositoryId",
            table: "Repositories");
    }
}
