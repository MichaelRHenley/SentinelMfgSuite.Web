using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentReportTransitionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AssessmentReportRevisions_AssessmentReportId_Id",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "Id" });

            migrationBuilder.CreateTable(
                name: "AssessmentReportTransitionHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentReportRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<short>(type: "smallint", nullable: false),
                    PreviousStatus = table.Column<short>(type: "smallint", nullable: true),
                    NewStatus = table.Column<short>(type: "smallint", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReplacedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacementRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssessmentReportTransitionHistory", x => x.Id);
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_EventShape", "(\"EventType\" = 1 AND \"PreviousStatus\" IS NULL AND \"NewStatus\" = 0) OR (\"EventType\" = 2 AND \"PreviousStatus\" IS NOT NULL AND ((\"PreviousStatus\" = 0 AND \"NewStatus\" = 1) OR (\"PreviousStatus\" = 1 AND \"NewStatus\" = 2) OR (\"PreviousStatus\" = 1 AND \"NewStatus\" = 5) OR (\"PreviousStatus\" = 2 AND \"NewStatus\" = 5) OR (\"PreviousStatus\" = 2 AND \"NewStatus\" = 3) OR (\"PreviousStatus\" = 3 AND \"NewStatus\" = 4)))");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_EventType", "\"EventType\" IN (1, 2)");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_Identifiers", "length(trim(\"ActorUserId\")) > 0 AND length(trim(\"CorrelationId\")) > 0");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_Reason", "(\"NewStatus\" = 5 AND \"Reason\" IS NOT NULL AND \"Reason\" = trim(\"Reason\") AND length(\"Reason\") BETWEEN 1 AND 2000) OR (\"NewStatus\" <> 5 AND \"Reason\" IS NULL)");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_ReplacedRevision", "CASE WHEN \"EventType\" = 2 AND \"PreviousStatus\" = 2 AND \"NewStatus\" = 3 THEN \"ReplacedRevisionId\" IS NULL OR \"ReplacedRevisionId\" <> \"AssessmentReportRevisionId\" ELSE \"ReplacedRevisionId\" IS NULL END");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_ReplacementRevision", "CASE WHEN \"EventType\" = 2 AND \"PreviousStatus\" = 3 AND \"NewStatus\" = 4 THEN \"ReplacementRevisionId\" IS NOT NULL AND \"ReplacementRevisionId\" <> \"AssessmentReportRevisionId\" ELSE \"ReplacementRevisionId\" IS NULL END");
                    table.CheckConstraint("CK_AssessmentReportTransitionHistory_StatusValues", "\"NewStatus\" BETWEEN 0 AND 5 AND (\"PreviousStatus\" IS NULL OR \"PreviousStatus\" BETWEEN 0 AND 5)");
                    table.ForeignKey(
                        name: "FK_AssessmentReportTransitionHistory_AssessmentReportRevisions~",
                        columns: x => new { x.AssessmentReportId, x.AssessmentReportRevisionId },
                        principalTable: "AssessmentReportRevisions",
                        principalColumns: new[] { "AssessmentReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssessmentReportTransitionHistory_AssessmentReportRevision~1",
                        columns: x => new { x.AssessmentReportId, x.ReplacedRevisionId },
                        principalTable: "AssessmentReportRevisions",
                        principalColumns: new[] { "AssessmentReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssessmentReportTransitionHistory_AssessmentReportRevision~2",
                        columns: x => new { x.AssessmentReportId, x.ReplacementRevisionId },
                        principalTable: "AssessmentReportRevisions",
                        principalColumns: new[] { "AssessmentReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportTransitionHistory_AssessmentReportId_Assess~",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportId", "AssessmentReportRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportTransitionHistory_AssessmentReportId_Occurr~",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportTransitionHistory_AssessmentReportId_Repla~1",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportId", "ReplacementRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportTransitionHistory_AssessmentReportId_Replac~",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportId", "ReplacedRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportTransitionHistory_AssessmentReportRevisionI~",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportRevisionId", "OccurredUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_AssessmentReportTransitionHistory_Revision_Created",
                table: "AssessmentReportTransitionHistory",
                column: "AssessmentReportRevisionId",
                unique: true,
                filter: "\"EventType\" = 1");

            migrationBuilder.CreateIndex(
                name: "UX_AssessmentReportTransitionHistory_Revision_NewStatus",
                table: "AssessmentReportTransitionHistory",
                columns: new[] { "AssessmentReportRevisionId", "NewStatus" },
                unique: true,
                filter: "\"EventType\" = 2");

            migrationBuilder.Sql(@"
CREATE FUNCTION ""AssessmentReportTransitionHistory_PreventMutation""() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'AssessmentReportTransitionHistory is append-only: % is not permitted.', TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER ""TR_AssessmentReportTransitionHistory_NoUpdateDelete""
    BEFORE UPDATE OR DELETE ON ""AssessmentReportTransitionHistory""
    FOR EACH ROW EXECUTE FUNCTION ""AssessmentReportTransitionHistory_PreventMutation""();

CREATE TRIGGER ""TR_AssessmentReportTransitionHistory_NoTruncate""
    BEFORE TRUNCATE ON ""AssessmentReportTransitionHistory""
    FOR EACH STATEMENT EXECUTE FUNCTION ""AssessmentReportTransitionHistory_PreventMutation""();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TR_AssessmentReportTransitionHistory_NoTruncate"" ON ""AssessmentReportTransitionHistory"";
DROP TRIGGER IF EXISTS ""TR_AssessmentReportTransitionHistory_NoUpdateDelete"" ON ""AssessmentReportTransitionHistory"";
");

            migrationBuilder.DropTable(
                name: "AssessmentReportTransitionHistory");

            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS ""AssessmentReportTransitionHistory_PreventMutation""();");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AssessmentReportRevisions_AssessmentReportId_Id",
                table: "AssessmentReportRevisions");
        }
    }
}
