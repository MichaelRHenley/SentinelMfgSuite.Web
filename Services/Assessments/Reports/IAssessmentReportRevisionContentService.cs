using System.Security.Claims;
using SentinelMfgSuite.Web.Models.Assessments.Reports;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public enum AssessmentReportRevisionContentState
{
    Retrieved,
    Saved,
    Forbidden,
    NotFound,
    NotDraft,
    ConcurrencyConflict,
    UnsupportedContentSchemaVersion,
    InvalidSourceData,
    ValidationFailed
}

public sealed record AssessmentReportRevisionContentResult(
    AssessmentReportRevisionContentState State,
    ConsultantReportContent? Content = null,
    Guid? ConcurrencyStamp = null,
    IReadOnlyList<ReportContentValidationIssue>? ValidationIssues = null,
    DateTime? UpdatedUtc = null)
{
    public bool Succeeded => State is AssessmentReportRevisionContentState.Retrieved or AssessmentReportRevisionContentState.Saved;
}

public interface IAssessmentReportRevisionContentService
{
    Task<AssessmentReportRevisionContentResult> GetAsync(Guid revisionId, ClaimsPrincipal caller, CancellationToken cancellationToken = default);

    Task<AssessmentReportRevisionContentResult> SaveDraftAsync(
        Guid revisionId,
        Guid expectedConcurrencyStamp,
        ConsultantReportContent content,
        ClaimsPrincipal caller,
        CancellationToken cancellationToken = default);
}
