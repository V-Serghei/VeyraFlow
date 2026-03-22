using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class allchange6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveFilePath",
                table: "RepositorySnapshots",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArchiveFileSizeBytes",
                table: "RepositorySnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "RepositorySnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "RepositorySnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RetentionAllowManualSnapshotCleanup",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RetentionAutomaticCompactionEnabled",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RetentionAutomaticCompactionWindowHours",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionMaintenanceWindowEndHour",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionMaintenanceWindowStartHour",
                table: "Repositories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetentionStorageMode",
                table: "Repositories",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "delete");

            migrationBuilder.CreateIndex(
                name: "IX_RepositorySnapshots_RepositoryId_IsArchived_CreatedAt",
                table: "RepositorySnapshots",
                columns: new[] { "RepositoryId", "IsArchived", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositorySnapshots_RepositoryId_IsArchived_CreatedAt",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "ArchiveFilePath",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "ArchiveFileSizeBytes",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "RepositorySnapshots");

            migrationBuilder.DropColumn(
                name: "RetentionAllowManualSnapshotCleanup",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionAutomaticCompactionEnabled",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionAutomaticCompactionWindowHours",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionMaintenanceWindowEndHour",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionMaintenanceWindowStartHour",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "RetentionStorageMode",
                table: "Repositories");
        }
    }
}
