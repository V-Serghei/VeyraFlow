using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260309180000_AddRepositoryRetentionPolicy")]
public partial class AddRepositoryRetentionPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // SQLite path is no-op.
            // Desktop startup applies self-heal for retention columns/index.
            return;
        }

        migrationBuilder.AddColumn<bool>(
            name: "RetentionEnabled",
            table: "Repositories",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "RetentionMaxAgeDays",
            table: "Repositories",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RetentionMaxSnapshots",
            table: "Repositories",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "RetentionMaxTotalSizeBytes",
            table: "Repositories",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RetentionTriggerFilter",
            table: "Repositories",
            type: "nvarchar(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RetentionRunIntervalMinutes",
            table: "Repositories",
            type: "int",
            nullable: false,
            defaultValue: 60);

        migrationBuilder.AddColumn<DateTime>(
            name: "RetentionLastRunAt",
            table: "Repositories",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RetentionLastStatus",
            table: "Repositories",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Repositories_RetentionEnabled_RetentionLastRunAt",
            table: "Repositories",
            columns: new[] { "RetentionEnabled", "RetentionLastRunAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.DropIndex(
            name: "IX_Repositories_RetentionEnabled_RetentionLastRunAt",
            table: "Repositories");

        migrationBuilder.DropColumn(name: "RetentionEnabled", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionMaxAgeDays", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionMaxSnapshots", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionMaxTotalSizeBytes", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionTriggerFilter", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionRunIntervalMinutes", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionLastRunAt", table: "Repositories");
        migrationBuilder.DropColumn(name: "RetentionLastStatus", table: "Repositories");
    }
}
