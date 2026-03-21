using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations;

public partial class AddUserProfileEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Email",
            table: "UserProfiles",
            type: "TEXT",
            maxLength: 320,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserProfiles_Email",
            table: "UserProfiles",
            column: "Email",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_UserProfiles_Email",
            table: "UserProfiles");

        migrationBuilder.DropColumn(
            name: "Email",
            table: "UserProfiles");
    }
}
