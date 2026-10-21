using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentReportCorrectionLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Read-only pre-flight audit: fails without modifying data if the new active-revision invariant is violated.
            // Lineage columns are new, so existing rows cannot carry invalid lineage; they are added as null.
            migrationBuilder.Sql(@"
LOCK TABLE ""AssessmentReportRevisions"" IN ACCESS EXCLUSIVE MODE;
DO $audit$
DECLARE
    multiple_active bigint;
    duplicate_numbers bigint;
    invalid_numbers bigint;
BEGIN
    SELECT count(*) INTO multiple_active FROM (
        SELECT ""AssessmentReportId"" FROM ""AssessmentReportRevisions"" WHERE ""Status"" IN (0, 1, 2)
        GROUP BY ""AssessmentReportId"" HAVING count(*) > 1) d;
    SELECT count(*) INTO duplicate_numbers FROM (
        SELECT 1 FROM ""AssessmentReportRevisions""
        GROUP BY ""AssessmentReportId"", ""RevisionNumber"" HAVING count(*) > 1) d;
    SELECT count(*) INTO invalid_numbers FROM ""AssessmentReportRevisions"" WHERE ""RevisionNumber"" < 1;

    IF multiple_active > 0 OR duplicate_numbers > 0 OR invalid_numbers > 0 THEN
        RAISE EXCEPTION 'AssessmentReportRevisions correction-lineage pre-migration audit failed: reports with multiple active (Draft/InReview/Approved) revisions=%, duplicate revision numbers=%, invalid revision numbers=%. No data was modified. Run docs/assessments/report-correction-lineage-preflight-audit.sql to identify the rows.',
            multiple_active, duplicate_numbers, invalid_numbers
            USING ERRCODE = 'check_violation';
    END IF;
END
$audit$;");

            migrationBuilder.AddColumn<Guid>(
                name: "BasedOnRevisionId",
                table: "AssessmentReportRevisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionReason",
                table: "AssessmentReportRevisions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportRevisions_Report_BasedOn",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "BasedOnRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_AssessmentReportRevisions_Report_ActiveRevision",
                table: "AssessmentReportRevisions",
                column: "AssessmentReportId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_CorrectionReason",
                table: "AssessmentReportRevisions",
                sql: "\"CorrectionReason\" IS NULL OR (\"BasedOnRevisionId\" IS NOT NULL AND length(trim(\"CorrectionReason\")) > 0 AND length(\"CorrectionReason\") <= 2000)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_NoSelfLineage",
                table: "AssessmentReportRevisions",
                sql: "\"BasedOnRevisionId\" IS NULL OR \"BasedOnRevisionId\" <> \"Id\"");

            migrationBuilder.AddForeignKey(
                name: "FK_AssessmentReportRevisions_BasedOn_SameReport",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "BasedOnRevisionId" },
                principalTable: "AssessmentReportRevisions",
                principalColumns: new[] { "AssessmentReportId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssessmentReportRevisions_BasedOn_SameReport",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropIndex(
                name: "IX_AssessmentReportRevisions_Report_BasedOn",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropIndex(
                name: "UX_AssessmentReportRevisions_Report_ActiveRevision",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_CorrectionReason",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_NoSelfLineage",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropColumn(
                name: "BasedOnRevisionId",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropColumn(
                name: "CorrectionReason",
                table: "AssessmentReportRevisions");
        }
    }
}
