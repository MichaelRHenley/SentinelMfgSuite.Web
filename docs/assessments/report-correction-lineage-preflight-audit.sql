-- Read-only pre-migration audit for AddAssessmentReportCorrectionLineage (PostgreSQL).
-- Status values: Draft=0, InReview=1, Approved=2, Issued=3, Superseded=4, Rejected=5.
-- Every query must return zero rows before the migration is applied. Nothing here modifies data.
-- BasedOnRevisionId/CorrectionReason are new columns, so no pre-existing lineage can be invalid.
-- The "source has a lower revision number" rule is enforced by the future creation service, not the database.

-- 1. Reports with more than one active (Draft/InReview/Approved) revision
SELECT "AssessmentReportId", count(*) AS active_revisions
FROM "AssessmentReportRevisions" WHERE "Status" IN (0, 1, 2)
GROUP BY "AssessmentReportId" HAVING count(*) > 1;

-- 2. Duplicate revision numbers within a report
SELECT "AssessmentReportId", "RevisionNumber", count(*)
FROM "AssessmentReportRevisions"
GROUP BY "AssessmentReportId", "RevisionNumber" HAVING count(*) > 1;

-- 3. Non-positive revision numbers
SELECT "Id", "AssessmentReportId", "RevisionNumber"
FROM "AssessmentReportRevisions" WHERE "RevisionNumber" < 1;
