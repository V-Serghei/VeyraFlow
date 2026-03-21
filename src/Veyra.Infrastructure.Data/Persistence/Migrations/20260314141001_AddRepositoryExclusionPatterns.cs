using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veyra.Infrastructure.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryExclusionPatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExclusionPatternsJson",
                table: "Repositories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExclusionPatternsJson",
                table: "Repositories");
        }
    }
}
