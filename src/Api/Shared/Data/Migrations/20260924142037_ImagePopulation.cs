using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImagePopulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pneumonia_source",
                table: "image_results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "population",
                table: "image_results",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pneumonia_source",
                table: "image_results");

            migrationBuilder.DropColumn(
                name: "population",
                table: "image_results");
        }
    }
}
