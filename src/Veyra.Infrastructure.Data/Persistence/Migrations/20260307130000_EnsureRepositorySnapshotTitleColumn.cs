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
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql("""
ALTER TABLE "RepositorySnapshots"
ADD COLUMN IF NOT EXISTS "Title" TEXT NULL;
""");

            return;
        }

        migrationBuilder.Sql("""
IF COL_LENGTH('RepositorySnapshots', 'Title') IS NULL
    ALTER TABLE [RepositorySnapshots] ADD [Title] nvarchar(256) NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
