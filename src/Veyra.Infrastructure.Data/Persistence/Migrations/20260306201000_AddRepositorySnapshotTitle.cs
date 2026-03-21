using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260306201000_AddRepositorySnapshotTitle")]
public partial class AddRepositorySnapshotTitle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Title",
            table: "RepositorySnapshots",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Title",
            table: "RepositorySnapshots");
    }
}

