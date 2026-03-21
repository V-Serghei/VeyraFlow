using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryAutoCaptureAndSnapshotTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCaptureFileVersions",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProtectCloudMetadata",
                table: "Repositories",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCaptureFileVersions",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "ProtectCloudMetadata",
                table: "Repositories");
        }
    }
}
