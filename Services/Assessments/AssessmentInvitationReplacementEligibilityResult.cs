namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentInvitationReplacementEligibilityResult(
    bool IsEligible,
    string? FailureCode,
    string? FailureMessage)
{
    public static AssessmentInvitationReplacementEligibilityResult Eligible()
        => new(true, null, null);

    public static AssessmentInvitationReplacementEligibilityResult Ineligible(string failureCode, string failureMessage)
        => new(false, failureCode, failureMessage);
}
