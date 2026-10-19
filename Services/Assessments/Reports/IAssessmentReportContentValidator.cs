using SentinelMfgSuite.Web.Models.Assessments.Reports;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;
using SentinelMfgSuite.Web.Services.Assessments.Scoring;

namespace SentinelMfgSuite.Web.Services.Assessments.Reports;

public enum AssessmentReportContentStage
{
    Draft,
    InReview,
    Approved,
    Issued
}

public enum ReportContentValidationSeverity
{
    Warning,
    Error
}

public sealed record ReportContentValidationIssue(
    string Code,
    ReportContentValidationSeverity Severity,
    string Path,
    string Message);

public sealed record AssessmentReportContentValidationContext(
    AssessmentReportContentStage Stage,
    QuestionnaireDefinition? QuestionnaireDefinition = null,
    QuestionnaireResponseV1? QuestionnaireResponse = null,
    ReadinessScoringSnapshot? ScoringSnapshot = null,
    IReadOnlySet<string>? ConsultantObservationIds = null);

public sealed record AssessmentReportContentValidationResult(
    IReadOnlyList<ReportContentValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ReportContentValidationSeverity.Error);
}

public interface IAssessmentReportContentValidator
{
    AssessmentReportContentValidationResult Validate(
        ConsultantReportContent content,
        AssessmentReportContentValidationContext context);
}
