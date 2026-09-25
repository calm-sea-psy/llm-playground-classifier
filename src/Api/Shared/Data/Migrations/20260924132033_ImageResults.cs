using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImageResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "image_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pneumonia = table.Column<string>(type: "jsonb", nullable: false),
                    findings = table.Column<string>(type: "jsonb", nullable: false),
                    pneumonia_probability = table.Column<double>(type: "double precision", nullable: true),
                    pneumonia_positive = table.Column<bool>(type: "boolean", nullable: true),
                    positive_count = table.Column<int>(type: "integer", nullable: false),
                    cnn_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    report_attempt = table.Column<string>(type: "jsonb", nullable: true),
                    llm_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    validation_passed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_image_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_image_results_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_image_results_job_id",
                table: "image_results",
                column: "job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "image_results");
        }
    }
}
