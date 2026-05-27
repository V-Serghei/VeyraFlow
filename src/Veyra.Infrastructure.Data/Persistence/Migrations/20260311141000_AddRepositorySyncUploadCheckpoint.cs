using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

public partial class AddRepositorySyncUploadCheckpoint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "UploadCheckpointNextIndex",
            table: "RepositorySyncQueueItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "UploadCheckpointTotal",
            table: "RepositorySyncQueueItems",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "UploadCheckpointSignature",
            table: "RepositorySyncQueueItems",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "UploadCheckpointNextIndex",
            table: "RepositorySyncQueueItems");

        migrationBuilder.DropColumn(
            name: "UploadCheckpointTotal",
            table: "RepositorySyncQueueItems");

        migrationBuilder.DropColumn(
            name: "UploadCheckpointSignature",
            table: "RepositorySyncQueueItems");
    }
}
