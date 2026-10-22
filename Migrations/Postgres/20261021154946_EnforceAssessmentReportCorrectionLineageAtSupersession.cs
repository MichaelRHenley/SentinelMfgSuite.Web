using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <summary>
    /// Stage 3c-5: the deferred supersession validator also requires replacement.BasedOnRevisionId = predecessor.Id.
    /// Legacy compatibility needs no marker: the validator only fires when a row is inserted as Superseded or becomes
    /// Superseded/changes its replacement link, and Superseded rows are terminal, so historical pairs are never revalidated
    /// while every new supersession is checked strictly (null lineage is rejected). Data is never modified.
    /// </summary>
    public partial class EnforceAssessmentReportCorrectionLineageAtSupersession : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $audit$
DECLARE
    wrong_lineage bigint;
    legacy_missing bigint;
BEGIN
    SELECT count(*) INTO wrong_lineage
    FROM ""AssessmentReportRevisions"" p
    JOIN ""AssessmentReportRevisions"" r ON r.""Id"" = p.""SupersededByRevisionId""
    WHERE p.""Status"" = 4 AND r.""BasedOnRevisionId"" IS NOT NULL AND r.""BasedOnRevisionId"" <> p.""Id"";
    SELECT count(*) INTO legacy_missing
    FROM ""AssessmentReportRevisions"" p
    JOIN ""AssessmentReportRevisions"" r ON r.""Id"" = p.""SupersededByRevisionId""
    WHERE p.""Status"" = 4 AND r.""BasedOnRevisionId"" IS NULL;
    IF wrong_lineage > 0 THEN
        RAISE EXCEPTION 'Correction-lineage enforcement pre-migration audit failed: % superseded revision(s) have a replacement whose non-null BasedOnRevisionId identifies a different revision. No data was modified. Run docs/assessments/report-correction-lineage-enforcement-preflight-audit.sql to identify the rows.', wrong_lineage
            USING ERRCODE = 'check_violation';
    END IF;
    IF legacy_missing > 0 THEN
        RAISE NOTICE 'Correction-lineage enforcement: % legacy supersession pair(s) have null replacement lineage; they are left unchanged and are not revalidated (Superseded is terminal).', legacy_missing;
    END IF;
END
$audit$;");

            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ""AssessmentReportRevision_ValidateSupersession""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
DECLARE
    replacement ""AssessmentReportRevisions""%ROWTYPE;
    current_row ""AssessmentReportRevisions""%ROWTYPE;
BEGIN
    SELECT * INTO current_row FROM ""AssessmentReportRevisions"" WHERE ""Id"" = NEW.""Id"";
    IF NOT FOUND OR current_row.""Status"" <> 4 THEN
        RETURN NULL;
    END IF;

    IF current_row.""SupersededByRevisionId"" IS NULL THEN
        RAISE EXCEPTION 'Superseded revision % has no replacement revision.', current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    SELECT * INTO replacement FROM ""AssessmentReportRevisions"" WHERE ""Id"" = current_row.""SupersededByRevisionId"";
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Replacement revision % for superseded revision % does not exist.', current_row.""SupersededByRevisionId"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""AssessmentReportId"" <> current_row.""AssessmentReportId"" THEN
        RAISE EXCEPTION 'Replacement revision % belongs to a different report than superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""RevisionNumber"" <= current_row.""RevisionNumber"" THEN
        RAISE EXCEPTION 'Replacement revision % must have a greater revision number than superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""BasedOnRevisionId"" IS DISTINCT FROM current_row.""Id"" THEN
        RAISE EXCEPTION 'Replacement revision % must be based on the revision it supersedes (%), but BasedOnRevisionId is %.', replacement.""Id"", current_row.""Id"", COALESCE(replacement.""BasedOnRevisionId""::text, 'NULL') USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""Status"" <> 3 THEN
        RAISE EXCEPTION 'Replacement revision % must be Issued at commit to supersede revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""IssuedUtc"" IS NULL OR current_row.""IssuedUtc"" IS NULL OR replacement.""IssuedUtc"" < current_row.""IssuedUtc"" THEN
        RAISE EXCEPTION 'Replacement revision % was issued before superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM ""AssessmentReportTransitionHistory"" p
        JOIN ""AssessmentReportTransitionHistory"" r ON r.""CorrelationId"" = p.""CorrelationId"" AND r.""AssessmentReportId"" = p.""AssessmentReportId""
        WHERE p.""AssessmentReportId"" = current_row.""AssessmentReportId""
          AND p.""AssessmentReportRevisionId"" = current_row.""Id""
          AND p.""EventType"" = 2 AND p.""PreviousStatus"" = 3 AND p.""NewStatus"" = 4
          AND p.""ReplacementRevisionId"" = replacement.""Id""
          AND r.""AssessmentReportRevisionId"" = replacement.""Id""
          AND r.""EventType"" = 2 AND r.""PreviousStatus"" = 2 AND r.""NewStatus"" = 3
          AND r.""ReplacedRevisionId"" = current_row.""Id""
          AND length(trim(p.""CorrelationId"")) > 0) THEN
        RAISE EXCEPTION 'Superseding revision % by % requires paired Issued->Superseded and Approved->Issued history events sharing one correlation id.', current_row.""Id"", replacement.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    RETURN NULL;
END
$fn$;

CREATE FUNCTION ""AssessmentReportRevision_PreventReplacementLineageChange""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
    IF EXISTS (SELECT 1 FROM ""AssessmentReportRevisions"" p
               WHERE p.""SupersededByRevisionId"" = OLD.""Id""
                 AND NEW.""BasedOnRevisionId"" IS DISTINCT FROM p.""Id"") THEN
        RAISE EXCEPTION 'Revision % is the replacement of a superseded revision: BasedOnRevisionId may only be set to the revision it supersedes.', OLD.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$fn$;

CREATE TRIGGER ""TR_AssessmentReportRevisions_ReplacementLineageIsFixed""
    BEFORE UPDATE ON ""AssessmentReportRevisions""
    FOR EACH ROW WHEN (OLD.""BasedOnRevisionId"" IS DISTINCT FROM NEW.""BasedOnRevisionId"")
    EXECUTE FUNCTION ""AssessmentReportRevision_PreventReplacementLineageChange""();
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TR_AssessmentReportRevisions_ReplacementLineageIsFixed"" ON ""AssessmentReportRevisions"";
DROP FUNCTION IF EXISTS ""AssessmentReportRevision_PreventReplacementLineageChange""();
CREATE OR REPLACE FUNCTION ""AssessmentReportRevision_ValidateSupersession""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
DECLARE
    replacement ""AssessmentReportRevisions""%ROWTYPE;
    current_row ""AssessmentReportRevisions""%ROWTYPE;
BEGIN
    SELECT * INTO current_row FROM ""AssessmentReportRevisions"" WHERE ""Id"" = NEW.""Id"";
    IF NOT FOUND OR current_row.""Status"" <> 4 THEN
        RETURN NULL;
    END IF;

    IF current_row.""SupersededByRevisionId"" IS NULL THEN
        RAISE EXCEPTION 'Superseded revision % has no replacement revision.', current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    SELECT * INTO replacement FROM ""AssessmentReportRevisions"" WHERE ""Id"" = current_row.""SupersededByRevisionId"";
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Replacement revision % for superseded revision % does not exist.', current_row.""SupersededByRevisionId"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""AssessmentReportId"" <> current_row.""AssessmentReportId"" THEN
        RAISE EXCEPTION 'Replacement revision % belongs to a different report than superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""RevisionNumber"" <= current_row.""RevisionNumber"" THEN
        RAISE EXCEPTION 'Replacement revision % must have a greater revision number than superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""Status"" <> 3 THEN
        RAISE EXCEPTION 'Replacement revision % must be Issued at commit to supersede revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    IF replacement.""IssuedUtc"" IS NULL OR current_row.""IssuedUtc"" IS NULL OR replacement.""IssuedUtc"" < current_row.""IssuedUtc"" THEN
        RAISE EXCEPTION 'Replacement revision % was issued before superseded revision %.', replacement.""Id"", current_row.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM ""AssessmentReportTransitionHistory"" p
        JOIN ""AssessmentReportTransitionHistory"" r ON r.""CorrelationId"" = p.""CorrelationId"" AND r.""AssessmentReportId"" = p.""AssessmentReportId""
        WHERE p.""AssessmentReportId"" = current_row.""AssessmentReportId""
          AND p.""AssessmentReportRevisionId"" = current_row.""Id""
          AND p.""EventType"" = 2 AND p.""PreviousStatus"" = 3 AND p.""NewStatus"" = 4
          AND p.""ReplacementRevisionId"" = replacement.""Id""
          AND r.""AssessmentReportRevisionId"" = replacement.""Id""
          AND r.""EventType"" = 2 AND r.""PreviousStatus"" = 2 AND r.""NewStatus"" = 3
          AND r.""ReplacedRevisionId"" = current_row.""Id""
          AND length(trim(p.""CorrelationId"")) > 0) THEN
        RAISE EXCEPTION 'Superseding revision % by % requires paired Issued->Superseded and Approved->Issued history events sharing one correlation id.', current_row.""Id"", replacement.""Id"" USING ERRCODE = 'check_violation';
    END IF;

    RETURN NULL;
END
$fn$;
");
        }
    }
}
