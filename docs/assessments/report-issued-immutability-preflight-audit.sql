-- Read-only pre-migration audit for EnforceIssuedAssessmentReportImmutability (PostgreSQL). Modifies nothing.
-- Classification of findings:
--   DEPLOYMENT-BLOCKING: none. The migration has no in-migration audit and the trigger fires only on UPDATE, so no existing row
--                        can make the migration fail or be rewritten by it.
--   HISTORICAL WARNING:  ISSUED_WITHOUT_FROZEN_CONTENT, INCONSISTENT_ISSUANCE_STAMPS (the latter is already prevented by check
--                        constraints, so rows indicate constraint bypass), SUPERSEDED_WITHOUT_VALID_REPLACEMENT.
--   REQUIRES AN EXPLICIT REMEDIATION DECISION: PARTIAL_PDF_METADATA, MALFORMED_PDF_METADATA, PDF_METADATA_ON_UNISSUED_REVISION.
--                        After deployment these rows can never be completed or corrected by UPDATE. Decide (before deploying) whether
--                        to leave them frozen or repair them with a reviewed, one-off, privileged script. This audit never repairs.
-- Status values: Draft=0, InReview=1, Approved=2, Issued=3, Superseded=4, Rejected=5.
-- The guard trigger fires only on UPDATE. Existing rows are never rewritten or rejected by installing it.
-- Rows reported below are NOT blocked from deployment, but they can never be changed through UPDATE afterwards
-- (other than the allowed Issued -> Superseded transition and one complete PDF finalization), so review them first.

-- 1. Issued/Superseded revisions with PARTIAL PDF metadata (cannot be completed after deployment: PDF metadata is write-once).
SELECT 'PARTIAL_PDF_METADATA' AS finding, "Id", "AssessmentReportId", "RevisionNumber", "Status",
	   ("PdfStorageKey" IS NOT NULL) AS has_key, ("PdfSha256" IS NOT NULL) AS has_sha, ("PdfLengthBytes" IS NOT NULL) AS has_length
FROM "AssessmentReportRevisions"
WHERE "Status" IN (3, 4)
  AND ("PdfStorageKey" IS NOT NULL OR "PdfSha256" IS NOT NULL OR "PdfLengthBytes" IS NOT NULL)
  AND NOT ("PdfStorageKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "PdfLengthBytes" IS NOT NULL);

-- 2. Issued/Superseded revisions whose PDF metadata is complete but malformed (blank key, SHA-256 not 64 hex chars, non-positive length).
SELECT 'MALFORMED_PDF_METADATA' AS finding, "Id", "AssessmentReportId", "RevisionNumber", "Status"
FROM "AssessmentReportRevisions"
WHERE "Status" IN (3, 4)
  AND "PdfStorageKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "PdfLengthBytes" IS NOT NULL
  AND (length(trim("PdfStorageKey")) = 0 OR "PdfSha256" !~ '^[0-9A-Fa-f]{64}$' OR "PdfLengthBytes" <= 0);

-- 3. PDF metadata on revisions that were never issued (Draft/InReview/Approved/Rejected). The guard forbids setting it at issuance.
SELECT 'PDF_METADATA_ON_UNISSUED_REVISION' AS finding, "Id", "AssessmentReportId", "RevisionNumber", "Status"
FROM "AssessmentReportRevisions"
WHERE "Status" NOT IN (3, 4)
  AND ("PdfStorageKey" IS NOT NULL OR "PdfSha256" IS NOT NULL OR "PdfLengthBytes" IS NOT NULL);

-- 4. Inconsistent issuance stamps on Issued/Superseded revisions (already prohibited by CK_AssessmentReportRevision_StatusStamps/StampPairs).
SELECT 'INCONSISTENT_ISSUANCE_STAMPS' AS finding, "Id", "AssessmentReportId", "RevisionNumber", "Status"
FROM "AssessmentReportRevisions"
WHERE "Status" IN (3, 4)
  AND ("IssuedUtc" IS NULL OR "IssuedByUserId" IS NULL OR "ApprovedUtc" IS NULL OR "ApprovedByUserId" IS NULL
	   OR "SubmittedForReviewUtc" IS NULL OR "SubmittedForReviewByUserId" IS NULL
	   OR "ApprovedUtc" < "SubmittedForReviewUtc" OR "IssuedUtc" < "ApprovedUtc");

-- 5. Issued/Superseded revisions without frozen content or scoring (informational: such rows are frozen as-is and cannot be completed later).
SELECT 'ISSUED_WITHOUT_FROZEN_CONTENT' AS finding, "Id", "AssessmentReportId", "RevisionNumber", "Status",
	   ("ConsultantContentJson" IS NULL) AS missing_content, ("ScoringSnapshot" IS NULL) AS missing_scoring,
	   ("ReportTemplateVersion" IS NULL) AS missing_template_version
FROM "AssessmentReportRevisions"
WHERE "Status" IN (3, 4)
  AND ("ConsultantContentJson" IS NULL OR "ScoringSnapshot" IS NULL OR "ReportTemplateVersion" IS NULL OR "ConsultantContentSchemaVersion" IS NULL);

-- 6. Superseded revisions violating existing supersession rules (also reported by the earlier lineage audits).
SELECT 'SUPERSEDED_WITHOUT_VALID_REPLACEMENT' AS finding, p."Id", p."AssessmentReportId", p."RevisionNumber"
FROM "AssessmentReportRevisions" p
LEFT JOIN "AssessmentReportRevisions" r ON r."Id" = p."SupersededByRevisionId"
WHERE p."Status" = 4
  AND (r."Id" IS NULL OR r."AssessmentReportId" <> p."AssessmentReportId" OR r."Status" NOT IN (3, 4));

-- 7. Summary counts.
SELECT "Status", count(*) AS revisions,
	   count(*) FILTER (WHERE "PdfStorageKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "PdfLengthBytes" IS NOT NULL) AS with_complete_pdf,
	   count(*) FILTER (WHERE ("PdfStorageKey" IS NOT NULL OR "PdfSha256" IS NOT NULL OR "PdfLengthBytes" IS NOT NULL)
						  AND NOT ("PdfStorageKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "PdfLengthBytes" IS NOT NULL)) AS with_partial_pdf
FROM "AssessmentReportRevisions"
GROUP BY "Status" ORDER BY "Status";
