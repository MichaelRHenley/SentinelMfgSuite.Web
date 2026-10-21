using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    /// <summary>
    /// Deferred database enforcement of atomic report supersession (Stage 3b-2).
    /// Status values: Approved=2, Issued=3, Superseded=4. History EventType 2 = Transition.
    /// Enforcement boundary: a supersession is validated at COMMIT only when a revision row is inserted as
    /// Superseded, or updated so that Status becomes Superseded or SupersededByRevisionId changes while Superseded.
    /// Updates that leave Status/SupersededByRevisionId unchanged on an existing Superseded row are not
    /// re-validated, so legacy Superseded rows without history are never rejected and no history is fabricated.
    /// A Superseded revision is terminal: an immediate BEFORE UPDATE trigger rejects any change to its status or link.
    /// A revision that is the replacement
    /// The non-deferrable partial unique Issued index forces the predecessor to leave Issued before the replacement
    /// becomes Issued, and this trigger requires both halves to be committed together.
    /// </summary>
    public partial class AddAssessmentReportSupersessionIntegrityTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION ""AssessmentReportRevision_ValidateSupersession""() RETURNS trigger
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

CREATE CONSTRAINT TRIGGER ""CTR_AssessmentReportRevisions_Supersession_Insert""
    AFTER INSERT ON ""AssessmentReportRevisions""
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.""Status"" = 4)
    EXECUTE FUNCTION ""AssessmentReportRevision_ValidateSupersession""();

CREATE CONSTRAINT TRIGGER ""CTR_AssessmentReportRevisions_Supersession_Update""
    AFTER UPDATE ON ""AssessmentReportRevisions""
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.""Status"" = 4 AND (OLD.""Status"" IS DISTINCT FROM NEW.""Status"" OR OLD.""SupersededByRevisionId"" IS DISTINCT FROM NEW.""SupersededByRevisionId""))
    EXECUTE FUNCTION ""AssessmentReportRevision_ValidateSupersession""();

CREATE FUNCTION ""AssessmentReportRevision_PreventSupersededChange""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
    RAISE EXCEPTION 'Superseded revision % is terminal: its status and replacement link cannot change.', OLD.""Id"" USING ERRCODE = 'check_violation';
END
$fn$;

CREATE TRIGGER ""TR_AssessmentReportRevisions_SupersededIsTerminal""
    BEFORE UPDATE ON ""AssessmentReportRevisions""
    FOR EACH ROW WHEN (OLD.""Status"" = 4 AND (NEW.""Status"" <> 4 OR NEW.""SupersededByRevisionId"" IS DISTINCT FROM OLD.""SupersededByRevisionId""))
    EXECUTE FUNCTION ""AssessmentReportRevision_PreventSupersededChange""();

CREATE FUNCTION ""AssessmentReportRevision_ValidateReplacementStaysIssued""() RETURNS trigger
LANGUAGE plpgsql AS $fn$
BEGIN
    IF EXISTS (SELECT 1 FROM ""AssessmentReportRevisions"" WHERE ""SupersededByRevisionId"" = NEW.""Id"")
       AND (SELECT ""Status"" FROM ""AssessmentReportRevisions"" WHERE ""Id"" = NEW.""Id"") NOT IN (3, 4) THEN
        RAISE EXCEPTION 'Revision % is the replacement of a superseded revision and may only leave Issued by becoming Superseded.', NEW.""Id"" USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END
$fn$;

CREATE CONSTRAINT TRIGGER ""CTR_AssessmentReportRevisions_ReplacementStaysIssued""
    AFTER UPDATE ON ""AssessmentReportRevisions""
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.""Status"" = 3 AND NEW.""Status"" NOT IN (3, 4))
    EXECUTE FUNCTION ""AssessmentReportRevision_ValidateReplacementStaysIssued""();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TR_AssessmentReportRevisions_SupersededIsTerminal"" ON ""AssessmentReportRevisions"";
DROP TRIGGER IF EXISTS ""CTR_AssessmentReportRevisions_ReplacementStaysIssued"" ON ""AssessmentReportRevisions"";
DROP TRIGGER IF EXISTS ""CTR_AssessmentReportRevisions_Supersession_Update"" ON ""AssessmentReportRevisions"";
DROP TRIGGER IF EXISTS ""CTR_AssessmentReportRevisions_Supersession_Insert"" ON ""AssessmentReportRevisions"";
DROP FUNCTION IF EXISTS ""AssessmentReportRevision_PreventSupersededChange""();
DROP FUNCTION IF EXISTS ""AssessmentReportRevision_ValidateReplacementStaysIssued""();
DROP FUNCTION IF EXISTS ""AssessmentReportRevision_ValidateSupersession""();
");
        }
    }
}
