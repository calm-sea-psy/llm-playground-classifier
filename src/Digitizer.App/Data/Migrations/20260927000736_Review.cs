using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Digitizer.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class Review : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReviewedAt",
                table: "documents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedFields",
                table: "documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewedSlots",
                table: "documents",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ReviewedFields",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ReviewedSlots",
                table: "documents");
        }
    }
}
