-- Read-only pre-migration audit for EnforceAssessmentReportCorrectionLineageAtSupersession (PostgreSQL). Modifies nothing.
-- Status values: Draft=0, InReview=1, Approved=2, Issued=3, Superseded=4, Rejected=5.
-- Classification of every Superseded predecessor (p) and its replacement (r = p.SupersededByRevisionId):
--   VALID_LINEAGE           r.BasedOnRevisionId = p.Id and all other supersession rules hold
--   LEGACY_MISSING_LINEAGE  r.BasedOnRevisionId IS NULL (historical, pre-lineage; grandfathered, never rewritten)
--   INCORRECT_LINEAGE       r.BasedOnRevisionId IS NOT NULL and <> p.Id (BLOCKS the migration; takes precedence over other classes)
--   OTHER_INTEGRITY_VIOLATION  missing/cross-report replacement, non-increasing revision number, replacement not Issued,
--                              issuance timestamp ordering, or missing paired history sharing a correlation id
-- Only INCORRECT_LINEAGE blocks the migration. Review OTHER_INTEGRITY_VIOLATION rows separately (they pre-date this stage).

-- 1. Per-pair classification
WITH classified AS (
  SELECT p."AssessmentReportId", p."Id" AS predecessor_id, p."RevisionNumber" AS predecessor_number,
         p."SupersededByRevisionId" AS replacement_id, r."RevisionNumber" AS replacement_number, r."BasedOnRevisionId",
         CASE
           WHEN r."BasedOnRevisionId" IS NOT NULL AND r."BasedOnRevisionId" <> p."Id" THEN 'INCORRECT_LINEAGE'
           WHEN r."Id" IS NULL
             OR r."AssessmentReportId" <> p."AssessmentReportId"
             OR r."RevisionNumber" <= p."RevisionNumber"
             OR r."Status" NOT IN (3, 4)
             OR r."IssuedUtc" IS NULL OR p."IssuedUtc" IS NULL OR r."IssuedUtc" < p."IssuedUtc"
             OR NOT EXISTS (
                  SELECT 1 FROM "AssessmentReportTransitionHistory" ph
                  JOIN "AssessmentReportTransitionHistory" rh
                    ON rh."CorrelationId" = ph."CorrelationId" AND rh."AssessmentReportId" = ph."AssessmentReportId"
                  WHERE ph."AssessmentReportRevisionId" = p."Id" AND ph."EventType" = 2 AND ph."PreviousStatus" = 3 AND ph."NewStatus" = 4
                    AND ph."ReplacementRevisionId" = r."Id"
                    AND rh."AssessmentReportRevisionId" = r."Id" AND rh."EventType" = 2 AND rh."PreviousStatus" = 2 AND rh."NewStatus" = 3
                    AND rh."ReplacedRevisionId" = p."Id" AND length(trim(ph."CorrelationId")) > 0)
             THEN 'OTHER_INTEGRITY_VIOLATION'
           WHEN r."BasedOnRevisionId" IS NULL THEN 'LEGACY_MISSING_LINEAGE'
           ELSE 'VALID_LINEAGE'
         END AS classification
  FROM "AssessmentReportRevisions" p
  LEFT JOIN "AssessmentReportRevisions" r ON r."Id" = p."SupersededByRevisionId"
  WHERE p."Status" = 4)
SELECT * FROM classified ORDER BY classification, "AssessmentReportId", predecessor_number;

-- 2. Summary counts per classification (INCORRECT_LINEAGE must be 0 before migrating)
-- (INCORRECT_LINEAGE takes precedence so it is never hidden behind another violation; it matches the migration's blocking rule exactly.)
WITH classified AS (
  SELECT CASE
           WHEN r."BasedOnRevisionId" IS NOT NULL AND r."BasedOnRevisionId" <> p."Id" THEN 'INCORRECT_LINEAGE'
           WHEN r."Id" IS NULL
             OR r."AssessmentReportId" <> p."AssessmentReportId"
             OR r."RevisionNumber" <= p."RevisionNumber"
             OR r."Status" NOT IN (3, 4)
             OR r."IssuedUtc" IS NULL OR p."IssuedUtc" IS NULL OR r."IssuedUtc" < p."IssuedUtc"
             OR NOT EXISTS (
                  SELECT 1 FROM "AssessmentReportTransitionHistory" ph
                  JOIN "AssessmentReportTransitionHistory" rh
                    ON rh."CorrelationId" = ph."CorrelationId" AND rh."AssessmentReportId" = ph."AssessmentReportId"
                  WHERE ph."AssessmentReportRevisionId" = p."Id" AND ph."EventType" = 2 AND ph."PreviousStatus" = 3 AND ph."NewStatus" = 4
                    AND ph."ReplacementRevisionId" = r."Id"
                    AND rh."AssessmentReportRevisionId" = r."Id" AND rh."EventType" = 2 AND rh."PreviousStatus" = 2 AND rh."NewStatus" = 3
                    AND rh."ReplacedRevisionId" = p."Id" AND length(trim(ph."CorrelationId")) > 0)
             THEN 'OTHER_INTEGRITY_VIOLATION'
           WHEN r."BasedOnRevisionId" IS NULL THEN 'LEGACY_MISSING_LINEAGE'
           ELSE 'VALID_LINEAGE'
         END AS classification
  FROM "AssessmentReportRevisions" p
  LEFT JOIN "AssessmentReportRevisions" r ON r."Id" = p."SupersededByRevisionId"
  WHERE p."Status" = 4)
SELECT classification, count(*) FROM classified GROUP BY classification ORDER BY classification;
-- 3. Issued/Approved replacements with lineage that does not match any predecessor that points at them
SELECT r."Id" AS revision_id, r."AssessmentReportId", r."BasedOnRevisionId", p."Id" AS superseded_predecessor
FROM "AssessmentReportRevisions" r
JOIN "AssessmentReportRevisions" p ON p."SupersededByRevisionId" = r."Id"
WHERE r."BasedOnRevisionId" IS NOT NULL AND r."BasedOnRevisionId" <> p."Id";

-- 4. Superseded revisions without a replacement link
SELECT "Id", "AssessmentReportId" FROM "AssessmentReportRevisions" WHERE "Status" = 4 AND "SupersededByRevisionId" IS NULL;
