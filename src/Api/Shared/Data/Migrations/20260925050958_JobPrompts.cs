using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "prompts",
                table: "jobs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prompts",
                table: "jobs");
        }
    }
}
