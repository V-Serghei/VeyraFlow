using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

public partial class AddUserProfileTokenLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.AddColumn<long>(
            name: "CloudSessionId",
            table: "UserProfiles",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "AccessTokenExpiresAtUtc",
            table: "UserProfiles",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RefreshToken",
            table: "UserProfiles",
            type: "nvarchar(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "RefreshTokenExpiresAtUtc",
            table: "UserProfiles",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.DropColumn(
            name: "CloudSessionId",
            table: "UserProfiles");

        migrationBuilder.DropColumn(
            name: "AccessTokenExpiresAtUtc",
            table: "UserProfiles");

        migrationBuilder.DropColumn(
            name: "RefreshToken",
            table: "UserProfiles");

        migrationBuilder.DropColumn(
            name: "RefreshTokenExpiresAtUtc",
            table: "UserProfiles");
    }
}
