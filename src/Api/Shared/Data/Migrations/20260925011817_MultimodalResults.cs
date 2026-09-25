using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultimodalResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "multimodal_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    report_text = table.Column<string>(type: "text", nullable: false),
                    report_summary = table.Column<string>(type: "jsonb", nullable: true),
                    concordance = table.Column<string>(type: "jsonb", nullable: false),
                    final_report = table.Column<string>(type: "jsonb", nullable: true),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    needs_review = table.Column<bool>(type: "boolean", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    llm_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_multimodal_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_multimodal_results_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_multimodal_results_job_id",
                table: "multimodal_results",
                column: "job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "multimodal_results");
        }
    }
}
