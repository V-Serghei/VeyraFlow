using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260307130000_EnsureRepositorySnapshotTitleColumn")]
public partial class EnsureRepositorySnapshotTitleColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite path is intentionally no-op: the Title column is created by
        // AddRepositorySnapshotTitle migration, and desktop startup contains
        // an additional self-heal for legacy databases.
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.Sql("""
IF COL_LENGTH('RepositorySnapshots', 'Title') IS NULL
    ALTER TABLE [RepositorySnapshots] ADD [Title] nvarchar(256) NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
