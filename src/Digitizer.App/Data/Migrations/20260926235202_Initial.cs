using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Digitizer.App.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackId = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    DocumentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackId = table.Column<string>(type: "TEXT", nullable: false),
                    PackVersion = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", nullable: true),
                    TypeWarning = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PurgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastExportId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_documents_exports_LastExportId",
                        column: x => x.LastExportId,
                        principalTable: "exports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "extractions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Engine = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    Pages = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceText = table.Column<string>(type: "TEXT", nullable: true),
                    OcrConfidence = table.Column<double>(type: "REAL", nullable: true),
                    Fields = table.Column<string>(type: "TEXT", nullable: true),
                    Issues = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<string>(type: "TEXT", nullable: true),
                    FinalSource = table.Column<string>(type: "TEXT", nullable: true),
                    FallbackUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    FallbackReason = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TextMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LlmMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_extractions_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExtractionId = table.Column<long>(type: "INTEGER", nullable: false),
                    FieldPath = table.Column<string>(type: "TEXT", nullable: false),
                    ExtractedValue = table.Column<string>(type: "TEXT", nullable: true),
                    CorrectedValue = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_corrections_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_corrections_extractions_ExtractionId",
                        column: x => x.ExtractionId,
                        principalTable: "extractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corrections_DocumentId",
                table: "corrections",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_corrections_ExtractionId",
                table: "corrections",
                column: "ExtractionId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_LastExportId",
                table: "documents",
                column: "LastExportId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_PackId_Sha256",
                table: "documents",
                columns: new[] { "PackId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_documents_Status",
                table: "documents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_extractions_DocumentId",
                table: "extractions",
                column: "DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "corrections");

            migrationBuilder.DropTable(
                name: "extractions");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "exports");
        }
    }
}
