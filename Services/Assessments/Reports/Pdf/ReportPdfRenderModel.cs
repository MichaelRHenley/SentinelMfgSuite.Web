using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports.Pdf;

/// <summary>
/// Everything the PDF renderer is allowed to read. Built only from a persisted report revision, its frozen
/// consultant content and its frozen scoring snapshot.
/// </summary>
public sealed record ReportPdfRenderModel(
    string TemplateVersion,
    bool TemplateVersionIsLegacyNullCompatibility,
    Guid ReportId,
    Guid RevisionId,
    int RevisionNumber,
    AssessmentReportRevisionStatus RevisionStatus,
    string? AssessmentTitle,
    string? SiteName,
    DateTime IssuedUtc,
    string? IssuedByUserId,
    DateTime? ApprovedUtc,
    string? ApprovedByUserId,
    Guid? BasedOnRevisionId,
    string? CorrectionReason,
    string QuestionnaireVersion,
    DateTime ResponseSubmittedUtc,
    string? ScoringContractVersion,
    string ScoringSnapshotSchemaVersion,
    string ContentSchemaVersion,
    ConsultantReportContent Content,
    ReadinessScoringSnapshot Snapshot);

/// <summary>Persisted inputs for the model builder. No database access happens after this is assembled.</summary>
public sealed record ReportPdfSource(
    AssessmentReport Report,
    AssessmentReportRevision Revision,
    string? AssessmentTitle,
    string? SiteName);

public sealed record ReportPdfValidationError(string Code, string Message);

public sealed record ReportPdfModelBuildResult(
    ReportPdfRenderModel? Model,
    IReadOnlyList<ReportPdfValidationError> Errors)
{
    public bool Succeeded => Model is not null && Errors.Count == 0;

    public static ReportPdfModelBuildResult Success(ReportPdfRenderModel model)
        => new(model, Array.Empty<ReportPdfValidationError>());

    public static ReportPdfModelBuildResult Failure(params ReportPdfValidationError[] errors)
        => new(null, errors);

    public static ReportPdfModelBuildResult Failure(IReadOnlyList<ReportPdfValidationError> errors)
        => new(null, errors);
}

public interface IReportPdfModelBuilder
{
    ReportPdfModelBuildResult Build(ReportPdfSource source);
}

public interface IReportPdfRenderer
{
    /// <summary>Renders the model to PDF bytes. Pure: no storage, HTTP or database access.</summary>
    byte[] Render(ReportPdfRenderModel model);
}
