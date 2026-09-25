using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Api.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_message = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ocr_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    page = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    avg_confidence = table.Column<double>(type: "double precision", nullable: true),
                    engine_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    request_elapsed_ms = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ocr_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_ocr_results_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created_at",
                table: "jobs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status",
                table: "jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_ocr_results_job_id_page",
                table: "ocr_results",
                columns: new[] { "job_id", "page" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ocr_results");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
