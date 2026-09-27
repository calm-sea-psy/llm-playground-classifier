using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Digitizer.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExportRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "exports",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "exports");
        }
    }
}
