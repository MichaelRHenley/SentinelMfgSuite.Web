using System.Security.Claims;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public enum AssessmentReportLifecycleState
{
    Succeeded,
    Forbidden,
    NotFound,
    InvalidTransition,
    InvalidReason,
    ConcurrencyConflict,
    InvalidSourceData,
    ValidationFailed,
    ActiveCorrectionExists,
    NoIssuedRevision
}

public sealed record AssessmentReportLifecycleResult(
    AssessmentReportLifecycleState State,
    AssessmentReportRevisionStatus? Status = null,
    Guid? ConcurrencyStamp = null,
    IReadOnlyList<ReportContentValidationIssue>? ValidationIssues = null,
    Guid? SupersededRevisionId = null,
    Guid? SupersededRevisionStamp = null)
{
    public bool Succeeded => State == AssessmentReportLifecycleState.Succeeded;
}

public sealed record AssessmentReportCorrectionResult(
    AssessmentReportLifecycleState State,
    Guid? RevisionId = null,
    int? RevisionNumber = null,
    Guid? ConcurrencyStamp = null,
    Guid? SourceRevisionId = null,
    Guid? SourceConcurrencyStamp = null)
{
    public bool Succeeded => State == AssessmentReportLifecycleState.Succeeded;
}

public interface IAssessmentReportLifecycleService
{
    /// <summary>
    /// Creates a Draft correction revision from the report's current Issued revision, which is resolved server-side and
    /// never modified. The expected stamp must match that Issued revision. Fails with ActiveCorrectionExists while a
    /// Draft, InReview or Approved revision exists. The optional reason is trimmed; blank or over-long values are InvalidReason.
    /// </summary>
    Task<AssessmentReportCorrectionResult> CreateCorrectionAsync(
        Guid reportId,
        Guid expectedSourceConcurrencyStamp,
        string? correctionReason,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);

    /// <summary>Draft to InReview.</summary>
    Task<AssessmentReportLifecycleResult> SubmitForReviewAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);

    /// <summary>InReview to Approved.</summary>
    Task<AssessmentReportLifecycleResult> ApproveAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approved to Issued. When the report has no other Issued revision this is initial issuance. When exactly one
    /// other Issued revision exists it is superseded atomically (replacement issuance) provided the target has a
    /// greater revision number; otherwise InvalidTransition is returned. The predecessor is never caller-supplied.
    /// On replacement success the result carries SupersededRevisionId and SupersededRevisionStamp.
    /// </summary>
    Task<AssessmentReportLifecycleResult> IssueAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);

    /// <summary>InReview to Rejected or Approved to Rejected. A reason is required.</summary>
    Task<AssessmentReportLifecycleResult> RejectAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        string? reason,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);
}
