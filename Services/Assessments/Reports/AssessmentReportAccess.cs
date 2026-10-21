using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

/// <summary>
/// Shared caller-identity, authorization and ownership logic for report services.
/// The actor is always derived from the authenticated principal, never from client input.
/// </summary>
internal static class AssessmentReportAccess
{
    internal const string AssessmentAdminRole = "AssessmentAdmin";

    public static bool TryGetCaller(ClaimsPrincipal caller, out string callerId)
    {
        callerId = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        return caller.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(callerId)
            && caller.IsInRole(AssessmentAdminRole);
    }

    public static bool IsOwnerAllowed(Assessment assessment, string callerId)
        => string.IsNullOrWhiteSpace(assessment.OwnerUserId)
            || string.Equals(assessment.OwnerUserId, callerId, StringComparison.Ordinal);

    /// <summary>
    /// Loads a revision with its report and assessment. Returns null when the revision is missing
    /// or the caller does not own the assessment (callers map both to NotFound).
    /// </summary>
    public static async Task<AssessmentReportRevision?> LoadOwnedRevisionAsync(
        ApplicationDbContext db,
        Guid revisionId,
        string callerId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = db.Set<AssessmentReportRevision>().AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }
        var revision = await query
            .Include(item => item.AssessmentReport)
                .ThenInclude(report => report.Assessment)
            .SingleOrDefaultAsync(item => item.Id == revisionId, cancellationToken);
        if (revision is null)
        {
            return null;
        }
        return IsOwnerAllowed(revision.AssessmentReport.Assessment, callerId) ? revision : null;
    }
}
