using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class EnforceIssuedAssessmentReportImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION ""AssessmentReportRevision_PreventIssuedMutation""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
DECLARE
    old_has_pdf boolean;
    new_has_pdf boolean;
    new_complete_pdf boolean;
BEGIN
    old_has_pdf := OLD.""PdfStorageKey"" IS NOT NULL OR OLD.""PdfSha256"" IS NOT NULL OR OLD.""PdfLengthBytes"" IS NOT NULL;
    new_has_pdf := NEW.""PdfStorageKey"" IS NOT NULL OR NEW.""PdfSha256"" IS NOT NULL OR NEW.""PdfLengthBytes"" IS NOT NULL;

    -- Revisions that are not yet Issued/Superseded (the Approved -> Issued transition): PDF metadata cannot be set at issuance.
    IF OLD.""Status"" NOT IN (3, 4) THEN
        IF new_has_pdf THEN
            RAISE EXCEPTION 'Revision % cannot carry PDF metadata when it becomes Issued; PDF metadata is finalized after issuance.', OLD.""Id"" USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;

    -- Evaluated against the OLD status so changing Status and protected content in one statement cannot evade the guard.
    IF NEW.""Status"" NOT IN (3, 4) OR (OLD.""Status"" = 4 AND NEW.""Status"" <> 4) THEN
        RAISE EXCEPTION 'Revision % is % and may only remain Issued or move from Issued to Superseded.', OLD.""Id"", CASE OLD.""Status"" WHEN 3 THEN 'Issued' ELSE 'Superseded' END USING ERRCODE = 'check_violation';
    END IF;

    IF NEW.""Id"" IS DISTINCT FROM OLD.""Id""
       OR NEW.""AssessmentReportId"" IS DISTINCT FROM OLD.""AssessmentReportId""
       OR NEW.""RevisionNumber"" IS DISTINCT FROM OLD.""RevisionNumber""
       OR NEW.""ExecutiveSummary"" IS DISTINCT FROM OLD.""ExecutiveSummary""
       OR NEW.""ScopeAndMethodology"" IS DISTINCT FROM OLD.""ScopeAndMethodology""
       OR NEW.""AssumptionsAndLimitations"" IS DISTINCT FROM OLD.""AssumptionsAndLimitations""
       OR NEW.""NextSteps"" IS DISTINCT FROM OLD.""NextSteps""
       OR NEW.""ScoringContractVersion"" IS DISTINCT FROM OLD.""ScoringContractVersion""
       OR NEW.""ScoringSnapshotSchemaVersion"" IS DISTINCT FROM OLD.""ScoringSnapshotSchemaVersion""
       OR NEW.""ScoringSnapshot"" IS DISTINCT FROM OLD.""ScoringSnapshot""
       OR NEW.""ReportTemplateVersion"" IS DISTINCT FROM OLD.""ReportTemplateVersion""
       OR NEW.""ConsultantContentJson"" IS DISTINCT FROM OLD.""ConsultantContentJson""
       OR NEW.""ConsultantContentSchemaVersion"" IS DISTINCT FROM OLD.""ConsultantContentSchemaVersion""
       OR NEW.""CreatedUtc"" IS DISTINCT FROM OLD.""CreatedUtc""
       OR NEW.""CreatedByUserId"" IS DISTINCT FROM OLD.""CreatedByUserId""
       OR NEW.""SubmittedForReviewUtc"" IS DISTINCT FROM OLD.""SubmittedForReviewUtc""
       OR NEW.""SubmittedForReviewByUserId"" IS DISTINCT FROM OLD.""SubmittedForReviewByUserId""
       OR NEW.""ApprovedUtc"" IS DISTINCT FROM OLD.""ApprovedUtc""
       OR NEW.""ApprovedByUserId"" IS DISTINCT FROM OLD.""ApprovedByUserId""
       OR NEW.""IssuedUtc"" IS DISTINCT FROM OLD.""IssuedUtc""
       OR NEW.""IssuedByUserId"" IS DISTINCT FROM OLD.""IssuedByUserId""
       OR NEW.""BasedOnRevisionId"" IS DISTINCT FROM OLD.""BasedOnRevisionId""
       OR NEW.""CorrectionReason"" IS DISTINCT FROM OLD.""CorrectionReason"" THEN
        RAISE EXCEPTION 'Revision % is % and its frozen content, scoring, lineage and issuance metadata cannot change.', OLD.""Id"", CASE OLD.""Status"" WHEN 3 THEN 'Issued' ELSE 'Superseded' END USING ERRCODE = 'check_violation';
    END IF;

    IF NEW.""PdfStorageKey"" IS DISTINCT FROM OLD.""PdfStorageKey""
       OR NEW.""PdfSha256"" IS DISTINCT FROM OLD.""PdfSha256""
       OR NEW.""PdfLengthBytes"" IS DISTINCT FROM OLD.""PdfLengthBytes"" THEN
        -- PDF metadata is write-once: only an Issued revision that has no PDF metadata may receive it, as one complete set.
        IF OLD.""Status"" <> 3 OR NEW.""Status"" <> 3 OR old_has_pdf THEN
            RAISE EXCEPTION 'Revision % PDF metadata can only be set once, on a revision that stays Issued.', OLD.""Id"" USING ERRCODE = 'check_violation';
        END IF;
        new_complete_pdf := NEW.""PdfStorageKey"" IS NOT NULL AND length(trim(NEW.""PdfStorageKey"")) > 0
            AND NEW.""PdfSha256"" IS NOT NULL AND NEW.""PdfSha256"" ~ '^[0-9A-Fa-f]{64}$'
            AND NEW.""PdfLengthBytes"" IS NOT NULL AND NEW.""PdfLengthBytes"" > 0;
        IF NOT new_complete_pdf THEN
            RAISE EXCEPTION 'Revision % PDF metadata must be complete: storage key, 64-character SHA-256 and positive length.', OLD.""Id"" USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    RETURN NEW;
END
$fn$;

CREATE TRIGGER ""TR_AssessmentReportRevisions_IssuedIsImmutable""
BEFORE UPDATE ON ""AssessmentReportRevisions""
FOR EACH ROW
WHEN (OLD.""Status"" IN (3, 4) OR NEW.""Status"" IN (3, 4))
EXECUTE FUNCTION ""AssessmentReportRevision_PreventIssuedMutation""();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TR_AssessmentReportRevisions_IssuedIsImmutable"" ON ""AssessmentReportRevisions"";
DROP FUNCTION IF EXISTS ""AssessmentReportRevision_PreventIssuedMutation""();
");
        }
    }
}
