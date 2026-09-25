using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineSettingsAndExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "combo_index",
                table: "jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "experiment_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                table: "jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "experiments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    combos = table.Column<string>(type: "jsonb", nullable: false),
                    doc_count = table.Column<int>(type: "integer", nullable: false),
                    environment = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_experiment_id",
                table: "jobs",
                column: "experiment_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiments_created_at",
                table: "experiments",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "experiments");

            migrationBuilder.DropTable(
                name: "pipeline_settings");

            migrationBuilder.DropIndex(
                name: "ix_jobs_experiment_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "combo_index",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "experiment_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "settings",
                table: "jobs");
        }
    }
}
