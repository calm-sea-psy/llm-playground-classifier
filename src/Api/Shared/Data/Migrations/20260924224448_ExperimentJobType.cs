using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExperimentJobType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_experiments_created_at",
                table: "experiments");

            migrationBuilder.AddColumn<string>(
                name: "job_type",
                table: "experiments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "text");

            migrationBuilder.CreateIndex(
                name: "ix_experiments_job_type_created_at",
                table: "experiments",
                columns: new[] { "job_type", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_experiments_job_type_created_at",
                table: "experiments");

            migrationBuilder.DropColumn(
                name: "job_type",
                table: "experiments");

            migrationBuilder.CreateIndex(
                name: "ix_experiments_created_at",
                table: "experiments",
                column: "created_at");
        }
    }
}
