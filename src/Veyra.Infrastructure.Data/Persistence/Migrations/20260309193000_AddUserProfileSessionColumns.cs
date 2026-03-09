using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260309193000_AddUserProfileSessionColumns")]
public partial class AddUserProfileSessionColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.AddColumn<long>(
            name: "CloudUserId",
            table: "UserProfiles",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AccessToken",
            table: "UserProfiles",
            type: "nvarchar(4096)",
            maxLength: 4096,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.DropColumn(name: "CloudUserId", table: "UserProfiles");
        migrationBuilder.DropColumn(name: "AccessToken", table: "UserProfiles");
    }
}
