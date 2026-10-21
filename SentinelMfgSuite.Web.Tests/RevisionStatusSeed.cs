using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Tests;

/// <summary>
/// Test seeding helper that forces a revision into a status together with the audit stamps the database
/// requires for that status, mirroring the permitted lifecycle paths. Superseded additionally creates a
/// same-report replacement: it is saved Approved, the original is then Superseded with the link, and the replacement is finally Issued, matching the ordered saves of a real replacement (the Issued index is not deferrable). Lifecycle history is not seeded.
/// </summary>
internal static class RevisionStatusSeed
{
    public static async Task SetAsync(ApplicationDbContext db, AssessmentReportRevision revision, AssessmentReportRevisionStatus status)
    {
        var now = DateTime.UtcNow;
        AssessmentReportRevision? replacement = null;
        if (status == AssessmentReportRevisionStatus.Superseded)
        {
            replacement = new AssessmentReportRevision
            {
                Id = Guid.NewGuid(),
                AssessmentReportId = revision.AssessmentReportId,
                RevisionNumber = revision.RevisionNumber + 1,
                Status = AssessmentReportRevisionStatus.Approved,
                SubmittedForReviewUtc = now,
                SubmittedForReviewByUserId = "seed-submitter",
                ApprovedUtc = now,
                ApprovedByUserId = "seed-approver",
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = "seed",
                UpdatedByUserId = "seed"
            };
            db.Set<AssessmentReportRevision>().Add(replacement);
            await db.SaveChangesAsync();
            revision.SupersededByRevisionId = replacement.Id;
        }

        revision.Status = status;
        var needsSubmitted = status != AssessmentReportRevisionStatus.Draft;
        var needsApproved = status is AssessmentReportRevisionStatus.Approved
            or AssessmentReportRevisionStatus.Issued or AssessmentReportRevisionStatus.Superseded;
        var needsIssued = status is AssessmentReportRevisionStatus.Issued or AssessmentReportRevisionStatus.Superseded;

        revision.SubmittedForReviewUtc = needsSubmitted ? now : null;
        revision.SubmittedForReviewByUserId = needsSubmitted ? "seed-submitter" : null;
        revision.ApprovedUtc = needsApproved ? now : null;
        revision.ApprovedByUserId = needsApproved ? "seed-approver" : null;
        revision.IssuedUtc = needsIssued ? now : null;
        revision.IssuedByUserId = needsIssued ? "seed-issuer" : null;

        await db.SaveChangesAsync();
    }
}
