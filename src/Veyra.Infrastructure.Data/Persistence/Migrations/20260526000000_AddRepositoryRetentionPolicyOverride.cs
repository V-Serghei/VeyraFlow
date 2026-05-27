using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260526000000_AddRepositoryRetentionPolicyOverride")]
public partial class AddRepositoryRetentionPolicyOverride : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.AddColumn<bool>(
            name: "RetentionPolicyOverrideEnabled",
            table: "Repositories",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE Repositories
            SET RetentionPolicyOverrideEnabled = 1
            WHERE RetentionPolicyOverrideEnabled = 0
              AND (
                  RetentionEnabled = 1
                  OR RetentionMaxAgeDays IS NOT NULL
                  OR RetentionMaxSnapshots IS NOT NULL
                  OR RetentionMaxTotalSizeBytes IS NOT NULL
                  OR RetentionTriggerFilter IS NOT NULL
                  OR RetentionStorageMode <> 'delete'
                  OR RetentionAllowManualSnapshotCleanup = 1
                  OR RetentionAutomaticCompactionEnabled = 1
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.DropColumn(
            name: "RetentionPolicyOverrideEnabled",
            table: "Repositories");
    }
}
