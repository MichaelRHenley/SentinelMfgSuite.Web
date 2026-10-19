using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentReportPersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssessmentReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionnaireVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResponseSubmittedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssessmentReports_AssessmentResponse_SubmittedResponseId",
                        column: x => x.SubmittedResponseId,
                        principalTable: "AssessmentResponse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssessmentReports_Assessment_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssessmentReportRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    ExecutiveSummary = table.Column<string>(type: "text", nullable: false),
                    ScopeAndMethodology = table.Column<string>(type: "text", nullable: false),
                    AssumptionsAndLimitations = table.Column<string>(type: "text", nullable: false),
                    NextSteps = table.Column<string>(type: "text", nullable: false),
                    ScoringContractVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ScoringSnapshotSchemaVersion = table.Column<int>(type: "integer", nullable: true),
                    ScoringSnapshot = table.Column<string>(type: "jsonb", nullable: true),
                    ReportTemplateVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubmittedForReviewUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedForReviewByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IssuedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IssuedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PdfStorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PdfSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PdfLengthBytes = table.Column<long>(type: "bigint", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentReportRevisions", x => x.Id);
                    table.CheckConstraint("CK_AssessmentReportRevision_RevisionNumber_Positive", "\"RevisionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_AssessmentReportRevisions_AssessmentReports_AssessmentRepor~",
                        column: x => x.AssessmentReportId,
                        principalTable: "AssessmentReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportRevisions_AssessmentReportId_RevisionNumber",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReports_AssessmentId",
                table: "AssessmentReports",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReports_SubmittedResponseId",
                table: "AssessmentReports",
                column: "SubmittedResponseId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssessmentReportRevisions");

            migrationBuilder.DropTable(
                name: "AssessmentReports");
        }
    }
}
