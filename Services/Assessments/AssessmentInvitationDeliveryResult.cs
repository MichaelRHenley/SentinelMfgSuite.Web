namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentInvitationDeliveryResult(
    bool Succeeded,
    string? FailureCode,
    string? FailureMessage,
    bool IsDevelopmentEmailSuppressed = false)
{
    public static AssessmentInvitationDeliveryResult Success()
        => new(true, null, null);

    public static AssessmentInvitationDeliveryResult Failure(
        string failureCode,
        string failureMessage,
        bool isDevelopmentEmailSuppressed = false)
        => new(false, failureCode, failureMessage, isDevelopmentEmailSuppressed);
}
