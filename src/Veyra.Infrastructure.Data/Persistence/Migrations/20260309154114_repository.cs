using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class repository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "WatchedDirectoryFormats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "WatchedDirectories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "SnapshotFileLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "SnapshotFileLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "RepositorySnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "RepositorySnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "RepositorySnapshotEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "RepositorySnapshotEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<bool>(
                name: "RetentionEnabled",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetentionLastRunAt",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetentionLastStatus",
                table: "Repositories",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionMaxAgeDays",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionMaxSnapshots",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RetentionMaxTotalSizeBytes",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionRunIntervalMinutes",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<string>(
                name: "RetentionTriggerFilter",
                table: "Repositories",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileVersionTextDiffs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FileVersionTextDiffs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "StorageFormatVersion",
                table: "FileVersionTextDiffs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileVersionTextDiffLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FileVersionTextDiffLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileVersionTextDiffHunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EndLineSequence",
                table: "FileVersionTextDiffHunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FileVersionTextDiffHunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "StartLineSequence",
                table: "FileVersionTextDiffHunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileVersions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FileVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileVersionBlocks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FileVersionBlocks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "FileSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "FileIdentities",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "FileIdentities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "D_WatchedFormats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileIdentityId",
                table: "SnapshotFileLinks",
                columns: new[] { "SnapshotId", "IsDeleted", "FileIdentityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshots_RepositoryId_IsDeleted_CreatedAt",
                table: "RepositorySnapshots",
                columns: new[] { "RepositoryId", "IsDeleted", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshotEntries_SnapshotId_IsDeleted_RelativePath",
                table: "RepositorySnapshotEntries",
                columns: new[] { "SnapshotId", "IsDeleted", "RelativePath" });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_RetentionEnabled_RetentionLastRunAt",
                table: "Repositories",
                columns: new[] { "RetentionEnabled", "RetentionLastRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_IsDeleted_MaxLines",
                table: "FileVersionTextDiffs",
                columns: new[] { "LeftFileVersionId", "RightFileVersionId", "IsDeleted", "MaxLines" });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionTextDiffLines_DiffId_IsDeleted_Sequence",
                table: "FileVersionTextDiffLines",
                columns: new[] { "DiffId", "IsDeleted", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionTextDiffHunks_DiffId_IsDeleted_Sequence",
                table: "FileVersionTextDiffHunks",
                columns: new[] { "DiffId", "IsDeleted", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersions_FileIdentityId_IsDeleted_CreatedAt",
                table: "FileVersions",
                columns: new[] { "FileIdentityId", "IsDeleted", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileVersionBlocks_FileVersionId_IsDeleted_Sequence",
                table: "FileVersionBlocks",
                columns: new[] { "FileVersionId", "IsDeleted", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SnapshotFileLinks_SnapshotId_IsDeleted_FileIdentityId",
                table: "SnapshotFileLinks");

            migrationBuilder.DropIndex(
                name: "IX_RepositorySnapshots_RepositoryId_IsDeleted_CreatedAt",
                table: "RepositorySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RepositorySnapshotEntries_SnapshotId_IsDeleted_RelativePath",
                table: "RepositorySnapshotEntries");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_RetentionEnabled_RetentionLastRunAt",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_FileVersionTextDiffs_LeftFileVersionId_RightFileVersionId_IsDeleted_MaxLines",
                table: "FileVersionTextDiffs");

            migrationBuilder.DropIndex(
                name: "IX_FileVersionTextDiffLines_DiffId_IsDeleted_Sequence",
                table: "FileVersionTextDiffLines");

            migrationBuilder.DropIndex(
                name: "IX_FileVersionTextDiffHunks_DiffId_IsDeleted_Sequence",
                table: "FileVersionTextDiffHunks");

            migrationBuilder.DropIndex(
                name: "IX_FileVersions_FileIdentityId_IsDeleted_CreatedAt",
                table: "FileVersions");

            migrationBuilder.DropIndex(
                name: "IX_FileVersionBlocks_FileVersionId_IsDeleted_Sequence",
                table: "FileVersionBlocks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "SnapshotFileLinks");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "SnapshotFileLinks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "RepositorySnapshotEntries");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "RepositorySnapshotEntries");

            migrationBuilder.DropColumn(
                name: "RetentionEnabled",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionLastRunAt",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionLastStatus",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionMaxAgeDays",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionMaxSnapshots",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionMaxTotalSizeBytes",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionRunIntervalMinutes",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionTriggerFilter",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileVersionTextDiffs");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FileVersionTextDiffs");

            migrationBuilder.DropColumn(
                name: "StorageFormatVersion",
                table: "FileVersionTextDiffs");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileVersionTextDiffLines");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FileVersionTextDiffLines");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileVersionTextDiffHunks");

            migrationBuilder.DropColumn(
                name: "EndLineSequence",
                table: "FileVersionTextDiffHunks");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FileVersionTextDiffHunks");

            migrationBuilder.DropColumn(
                name: "StartLineSequence",
                table: "FileVersionTextDiffHunks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FileVersions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileVersionBlocks");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FileVersionBlocks");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FileIdentities");

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "WatchedDirectoryFormats",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "WatchedDirectories",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "FileSnapshots",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "FileIdentities",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "D_WatchedFormats",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: false);
        }
    }
}
