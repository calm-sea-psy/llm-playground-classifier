using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "extraction_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    classify_ms = table.Column<int>(type: "integer", nullable: false),
                    final_source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    fallback_used = table.Column<bool>(type: "boolean", nullable: false),
                    fallback_reason = table.Column<string>(type: "text", nullable: true),
                    validation_passed = table.Column<bool>(type: "boolean", nullable: true),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: true),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<string>(type: "jsonb", nullable: false),
                    reading_text = table.Column<string>(type: "text", nullable: false),
                    llm_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extraction_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_extraction_results_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_results_job_id",
                table: "extraction_results",
                column: "job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extraction_results_model_document_type",
                table: "extraction_results",
                columns: new[] { "model", "document_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extraction_results");

            migrationBuilder.DropColumn(
                name: "model",
                table: "jobs");
        }
    }
}
