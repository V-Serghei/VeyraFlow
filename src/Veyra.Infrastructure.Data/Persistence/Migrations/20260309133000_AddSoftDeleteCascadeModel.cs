using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Veyra.Infrastructure.Data.Persistence;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

[DbContext(typeof(VeyraDbContext))]
[Migration("20260309133000_AddSoftDeleteCascadeModel")]
public partial class AddSoftDeleteCascadeModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // SQLite path is intentionally no-op.
            // Desktop startup applies a safe self-heal that adds missing soft-delete columns/indexes.
            return;
        }

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileIdentities",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "FileVersions",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileVersions",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "FileVersionBlocks",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileVersionBlocks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "RepositorySnapshots",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "RepositorySnapshots",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "RepositorySnapshotEntries",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "RepositorySnapshotEntries",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "SnapshotFileLinks",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "SnapshotFileLinks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "FileVersionTextDiffs",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileVersionTextDiffs",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "FileVersionTextDiffHunks",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileVersionTextDiffHunks",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsDeleted",
            table: "FileVersionTextDiffLines",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime?>(
            name: "DeletedAt",
            table: "FileVersionTextDiffLines",
            type: "datetime2",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_FileVersions_FileIdentityId_IsDeleted_CreatedAt",
            table: "FileVersions",
            columns: new[] { "FileIdentityId", "IsDeleted", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionBlocks_FileVersionId_IsDeleted_Sequence",
            table: "FileVersionBlocks",
            columns: new[] { "FileVersionId", "IsDeleted", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_RepositorySnapshots_RepositoryId_IsDeleted_CreatedAt",
            table: "RepositorySnapshots",
            columns: new[] { "RepositoryId", "IsDeleted", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RepositorySnapshotEntries_SnapshotId_IsDeleted_RelativePath",
            table: "RepositorySnapshotEntries",
            columns: new[] { "SnapshotId", "IsDeleted", "RelativePath" });

        migrationBuilder.CreateIndex(
            name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileIdentityId",
            table: "SnapshotFileLinks",
            columns: new[] { "SnapshotId", "IsDeleted", "FileIdentityId" });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_IsDeleted_MaxLines",
            table: "FileVersionTextDiffs",
            columns: new[] { "LeftFileVersionId", "RightFileVersionId", "IsDeleted", "MaxLines" });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffHunks_DiffId_IsDeleted_Sequence",
            table: "FileVersionTextDiffHunks",
            columns: new[] { "DiffId", "IsDeleted", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_FileVersionTextDiffLines_DiffId_IsDeleted_Sequence",
            table: "FileVersionTextDiffLines",
            columns: new[] { "DiffId", "IsDeleted", "Sequence" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        migrationBuilder.DropIndex(
            name: "IX_FileVersions_FileIdentityId_IsDeleted_CreatedAt",
            table: "FileVersions");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionBlocks_FileVersionId_IsDeleted_Sequence",
            table: "FileVersionBlocks");

        migrationBuilder.DropIndex(
            name: "IX_RepositorySnapshots_RepositoryId_IsDeleted_CreatedAt",
            table: "RepositorySnapshots");

        migrationBuilder.DropIndex(
            name: "IX_RepositorySnapshotEntries_SnapshotId_IsDeleted_RelativePath",
            table: "RepositorySnapshotEntries");

        migrationBuilder.DropIndex(
            name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileIdentityId",
            table: "SnapshotFileLinks");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_IsDeleted_MaxLines",
            table: "FileVersionTextDiffs");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionTextDiffHunks_DiffId_IsDeleted_Sequence",
            table: "FileVersionTextDiffHunks");

        migrationBuilder.DropIndex(
            name: "IX_FileVersionTextDiffLines_DiffId_IsDeleted_Sequence",
            table: "FileVersionTextDiffLines");

        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileIdentities");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "FileVersions");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileVersions");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "FileVersionBlocks");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileVersionBlocks");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "RepositorySnapshots");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "RepositorySnapshots");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "RepositorySnapshotEntries");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "RepositorySnapshotEntries");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "SnapshotFileLinks");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "SnapshotFileLinks");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "FileVersionTextDiffs");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileVersionTextDiffs");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "FileVersionTextDiffHunks");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileVersionTextDiffHunks");

        migrationBuilder.DropColumn(name: "IsDeleted", table: "FileVersionTextDiffLines");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "FileVersionTextDiffLines");
    }
}
