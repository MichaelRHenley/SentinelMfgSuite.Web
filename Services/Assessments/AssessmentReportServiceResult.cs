using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments;

public enum AssessmentReportServiceState
{
    Created = 0,
    AlreadyExists = 1,
    AssessmentNotFound = 2,
    ResponseNotFound = 3,
    ResponseAssessmentMismatch = 4,
    ResponseNotSubmitted = 5,
    InvalidResponse = 6,
    UnsupportedQuestionnaireVersion = 7,
    NotFound = 8,
    Forbidden = 9,
    Retrieved = 10
}

public sealed record AssessmentReportServiceResult(
    AssessmentReportServiceState State,
    AssessmentReport? Report = null)
{
    public bool Succeeded => State is AssessmentReportServiceState.Created or AssessmentReportServiceState.AlreadyExists or AssessmentReportServiceState.Retrieved;

    public static AssessmentReportServiceResult Created(AssessmentReport report)
        => new(AssessmentReportServiceState.Created, report);

    public static AssessmentReportServiceResult AlreadyExists(AssessmentReport report)
        => new(AssessmentReportServiceState.AlreadyExists, report);

    public static AssessmentReportServiceResult Retrieved(AssessmentReport report)
        => new(AssessmentReportServiceState.Retrieved, report);

    public static AssessmentReportServiceResult Failure(AssessmentReportServiceState state)
        => new(state);
}
