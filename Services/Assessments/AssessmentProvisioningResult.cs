namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentProvisioningResult(
    bool Succeeded,
    Guid? AssessmentId,
    Guid? InvitationId,
    string? FailureCode,
    string? FailureMessage)
{
    public static AssessmentProvisioningResult Success(Guid assessmentId, Guid? invitationId = null)
        => new(true, assessmentId, invitationId, null, null);

    public static AssessmentProvisioningResult Failure(string failureCode, string failureMessage, Guid? assessmentId = null, Guid? invitationId = null)
        => new(false, assessmentId, invitationId, failureCode, failureMessage);
}
