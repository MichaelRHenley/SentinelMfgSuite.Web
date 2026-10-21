-- Read-only pre-migration audit for AddAssessmentReportIssuanceSchema (PostgreSQL).
-- Status values: Draft=0, InReview=1, Approved=2, Issued=3, Superseded=4, Rejected=5.
-- Every query must return zero rows before the migration is applied. Nothing here modifies data.

-- 1. Invalid status values
SELECT "Id", "AssessmentReportId", "RevisionNumber", "Status"
FROM "AssessmentReportRevisions" WHERE "Status" NOT BETWEEN 0 AND 5;

-- 2. Timestamp/actor pairs not populated together, or blank actor identifiers
SELECT "Id", "AssessmentReportId", "RevisionNumber", "Status"
FROM "AssessmentReportRevisions"
WHERE ("SubmittedForReviewUtc" IS NULL) <> ("SubmittedForReviewByUserId" IS NULL)
   OR ("ApprovedUtc" IS NULL) <> ("ApprovedByUserId" IS NULL)
   OR ("IssuedUtc" IS NULL) <> ("IssuedByUserId" IS NULL)
   OR ("SubmittedForReviewByUserId" IS NOT NULL AND length(trim("SubmittedForReviewByUserId")) = 0)
   OR ("ApprovedByUserId" IS NOT NULL AND length(trim("ApprovedByUserId")) = 0)
   OR ("IssuedByUserId" IS NOT NULL AND length(trim("IssuedByUserId")) = 0);

-- 3. Invalid status/audit-stamp combinations
SELECT "Id", "AssessmentReportId", "RevisionNumber", "Status",
       "SubmittedForReviewUtc" IS NOT NULL AS has_submitted,
       "ApprovedUtc" IS NOT NULL AS has_approved,
       "IssuedUtc" IS NOT NULL AS has_issued
FROM "AssessmentReportRevisions"
WHERE "Status" BETWEEN 0 AND 5 AND NOT (
    ("Status" = 0 AND "SubmittedForReviewUtc" IS NULL AND "ApprovedUtc" IS NULL AND "IssuedUtc" IS NULL) OR
    ("Status" = 1 AND "SubmittedForReviewUtc" IS NOT NULL AND "ApprovedUtc" IS NULL AND "IssuedUtc" IS NULL) OR
    ("Status" = 2 AND "SubmittedForReviewUtc" IS NOT NULL AND "ApprovedUtc" IS NOT NULL AND "IssuedUtc" IS NULL) OR
    ("Status" IN (3, 4) AND "SubmittedForReviewUtc" IS NOT NULL AND "ApprovedUtc" IS NOT NULL AND "IssuedUtc" IS NOT NULL) OR
    ("Status" = 5 AND "SubmittedForReviewUtc" IS NOT NULL AND "IssuedUtc" IS NULL));

-- 4. Reports with more than one current Issued revision
SELECT "AssessmentReportId", count(*) AS issued_revisions
FROM "AssessmentReportRevisions" WHERE "Status" = 3
GROUP BY "AssessmentReportId" HAVING count(*) > 1;

-- 5. Existing supersession relationships. The link column does not exist yet, so any Superseded row
--    cannot satisfy the new link constraint and must be reviewed manually.
SELECT "Id", "AssessmentReportId", "RevisionNumber", "IssuedUtc"
FROM "AssessmentReportRevisions" WHERE "Status" = 4;

-- 6. Missing or inconsistent historical metadata (informational; the migration does not enforce these,
--    and legacy revisions without history are not repaired or fabricated)
SELECT r."Id", r."AssessmentReportId", r."RevisionNumber", r."Status"
FROM "AssessmentReportRevisions" r
WHERE NOT EXISTS (SELECT 1 FROM "AssessmentReportTransitionHistory" h
                  WHERE h."AssessmentReportRevisionId" = r."Id" AND h."EventType" = 1);

SELECT r."Id", r."AssessmentReportId", r."RevisionNumber", r."Status" AS revision_status,
       (SELECT h."NewStatus" FROM "AssessmentReportTransitionHistory" h
        WHERE h."AssessmentReportRevisionId" = r."Id"
        ORDER BY h."OccurredUtc" DESC, h."Id" DESC LIMIT 1) AS last_history_status
FROM "AssessmentReportRevisions" r
WHERE EXISTS (SELECT 1 FROM "AssessmentReportTransitionHistory" h WHERE h."AssessmentReportRevisionId" = r."Id")
  AND r."Status" <> (SELECT h."NewStatus" FROM "AssessmentReportTransitionHistory" h
                     WHERE h."AssessmentReportRevisionId" = r."Id"
                     ORDER BY h."OccurredUtc" DESC, h."Id" DESC LIMIT 1);