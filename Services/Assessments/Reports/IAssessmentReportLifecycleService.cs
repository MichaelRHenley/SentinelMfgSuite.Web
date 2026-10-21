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
    ValidationFailed
}

public sealed record AssessmentReportLifecycleResult(
    AssessmentReportLifecycleState State,
    AssessmentReportRevisionStatus? Status = null,
    Guid? ConcurrencyStamp = null,
    IReadOnlyList<ReportContentValidationIssue>? ValidationIssues = null)
{
    public bool Succeeded => State == AssessmentReportLifecycleState.Succeeded;
}

public interface IAssessmentReportLifecycleService
{
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

    /// <summary>InReview to Rejected or Approved to Rejected. A reason is required.</summary>
    Task<AssessmentReportLifecycleResult> RejectAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        string? reason,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);
}
