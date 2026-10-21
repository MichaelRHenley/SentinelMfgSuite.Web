using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAssessmentReportIssuanceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-flight audit: fail clearly (without modifying data) if existing rows violate a constraint introduced below.
            // The table is locked first so the audit result cannot be invalidated by concurrent writers before the constraints are added.
            // Equivalent read-only queries: docs/assessments/report-issuance-schema-preflight-audit.sql
            migrationBuilder.Sql(@"
LOCK TABLE ""AssessmentReportRevisions"" IN ACCESS EXCLUSIVE MODE;
DO $audit$
DECLARE
    invalid_status bigint;
    invalid_pairs bigint;
    invalid_stamps bigint;
    duplicate_issued bigint;
    superseded_rows bigint;
BEGIN
    SELECT count(*) INTO invalid_status FROM ""AssessmentReportRevisions"" WHERE ""Status"" NOT BETWEEN 0 AND 5;
    SELECT count(*) INTO invalid_pairs FROM ""AssessmentReportRevisions""
        WHERE (""SubmittedForReviewUtc"" IS NULL) <> (""SubmittedForReviewByUserId"" IS NULL)
           OR (""ApprovedUtc"" IS NULL) <> (""ApprovedByUserId"" IS NULL)
           OR (""IssuedUtc"" IS NULL) <> (""IssuedByUserId"" IS NULL)
           OR (""SubmittedForReviewByUserId"" IS NOT NULL AND length(trim(""SubmittedForReviewByUserId"")) = 0)
           OR (""ApprovedByUserId"" IS NOT NULL AND length(trim(""ApprovedByUserId"")) = 0)
           OR (""IssuedByUserId"" IS NOT NULL AND length(trim(""IssuedByUserId"")) = 0);
    SELECT count(*) INTO invalid_stamps FROM ""AssessmentReportRevisions""
        WHERE ""Status"" BETWEEN 0 AND 5 AND NOT (
            (""Status"" = 0 AND ""SubmittedForReviewUtc"" IS NULL AND ""ApprovedUtc"" IS NULL AND ""IssuedUtc"" IS NULL) OR
            (""Status"" = 1 AND ""SubmittedForReviewUtc"" IS NOT NULL AND ""ApprovedUtc"" IS NULL AND ""IssuedUtc"" IS NULL) OR
            (""Status"" = 2 AND ""SubmittedForReviewUtc"" IS NOT NULL AND ""ApprovedUtc"" IS NOT NULL AND ""IssuedUtc"" IS NULL) OR
            (""Status"" IN (3, 4) AND ""SubmittedForReviewUtc"" IS NOT NULL AND ""ApprovedUtc"" IS NOT NULL AND ""IssuedUtc"" IS NOT NULL) OR
            (""Status"" = 5 AND ""SubmittedForReviewUtc"" IS NOT NULL AND ""IssuedUtc"" IS NULL));
    SELECT count(*) INTO duplicate_issued FROM (
        SELECT ""AssessmentReportId"" FROM ""AssessmentReportRevisions"" WHERE ""Status"" = 3
        GROUP BY ""AssessmentReportId"" HAVING count(*) > 1) d;
    SELECT count(*) INTO superseded_rows FROM ""AssessmentReportRevisions"" WHERE ""Status"" = 4;

    IF invalid_status > 0 OR invalid_pairs > 0 OR invalid_stamps > 0 OR duplicate_issued > 0 OR superseded_rows > 0 THEN
        RAISE EXCEPTION 'AssessmentReportRevisions pre-migration audit failed: invalid status=%, invalid stamp pairs=%, invalid status/stamp combinations=%, reports with duplicate Issued revisions=%, Superseded rows without a replacement link=%. No data was modified. Run docs/assessments/report-issuance-schema-preflight-audit.sql to identify the rows.',
            invalid_status, invalid_pairs, invalid_stamps, duplicate_issued, superseded_rows
            USING ERRCODE = 'check_violation';
    END IF;
END
$audit$;");
            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByRevisionId",
                table: "AssessmentReportRevisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssessmentReportRevisions_Report_SupersededBy",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "SupersededByRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_AssessmentReportRevisions_Report_CurrentIssued",
                table: "AssessmentReportRevisions",
                column: "AssessmentReportId",
                unique: true,
                filter: "\"Status\" = 3");

            migrationBuilder.CreateIndex(
                name: "UX_AssessmentReportRevisions_SupersededBy",
                table: "AssessmentReportRevisions",
                column: "SupersededByRevisionId",
                unique: true,
                filter: "\"SupersededByRevisionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_NoSelfSupersession",
                table: "AssessmentReportRevisions",
                sql: "\"SupersededByRevisionId\" IS NULL OR \"SupersededByRevisionId\" <> \"Id\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_StampPairs",
                table: "AssessmentReportRevisions",
                sql: "((\"SubmittedForReviewUtc\" IS NULL) = (\"SubmittedForReviewByUserId\" IS NULL)) AND ((\"ApprovedUtc\" IS NULL) = (\"ApprovedByUserId\" IS NULL)) AND ((\"IssuedUtc\" IS NULL) = (\"IssuedByUserId\" IS NULL)) AND (\"SubmittedForReviewByUserId\" IS NULL OR length(trim(\"SubmittedForReviewByUserId\")) > 0) AND (\"ApprovedByUserId\" IS NULL OR length(trim(\"ApprovedByUserId\")) > 0) AND (\"IssuedByUserId\" IS NULL OR length(trim(\"IssuedByUserId\")) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_StatusStamps",
                table: "AssessmentReportRevisions",
                sql: "(\"Status\" = 0 AND \"SubmittedForReviewUtc\" IS NULL AND \"ApprovedUtc\" IS NULL AND \"IssuedUtc\" IS NULL) OR (\"Status\" = 1 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NULL AND \"IssuedUtc\" IS NULL) OR (\"Status\" = 2 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NOT NULL AND \"IssuedUtc\" IS NULL) OR (\"Status\" IN (3, 4) AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"ApprovedUtc\" IS NOT NULL AND \"IssuedUtc\" IS NOT NULL) OR (\"Status\" = 5 AND \"SubmittedForReviewUtc\" IS NOT NULL AND \"IssuedUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_StatusValues",
                table: "AssessmentReportRevisions",
                sql: "\"Status\" BETWEEN 0 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AssessmentReportRevision_SupersededLink",
                table: "AssessmentReportRevisions",
                sql: "(\"Status\" = 4) = (\"SupersededByRevisionId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_AssessmentReportRevisions_SupersededBy_SameReport",
                table: "AssessmentReportRevisions",
                columns: new[] { "AssessmentReportId", "SupersededByRevisionId" },
                principalTable: "AssessmentReportRevisions",
                principalColumns: new[] { "AssessmentReportId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssessmentReportRevisions_SupersededBy_SameReport",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropIndex(
                name: "IX_AssessmentReportRevisions_Report_SupersededBy",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropIndex(
                name: "UX_AssessmentReportRevisions_Report_CurrentIssued",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropIndex(
                name: "UX_AssessmentReportRevisions_SupersededBy",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_NoSelfSupersession",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_StampPairs",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_StatusStamps",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_StatusValues",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AssessmentReportRevision_SupersededLink",
                table: "AssessmentReportRevisions");

            migrationBuilder.DropColumn(
                name: "SupersededByRevisionId",
                table: "AssessmentReportRevisions");
        }
    }
}
