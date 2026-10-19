namespace SentinelMfgSuite.Web.Services.Assessments;

internal sealed record AssessmentManualProvisioningAtomicResult(
    bool Succeeded,
    Guid? AssessmentId,
    Guid? InvitationId,
    string? InvitationToken,
    DateTime? InvitationExpiresUtc,
    string? FailureCode,
    string? FailureMessage)
{
    public static AssessmentManualProvisioningAtomicResult Success(Guid assessmentId, Guid invitationId, string invitationToken, DateTime invitationExpiresUtc)
        => new(true, assessmentId, invitationId, invitationToken, invitationExpiresUtc, null, null);

    public static AssessmentManualProvisioningAtomicResult Failure(string failureCode, string failureMessage)
        => new(false, null, null, null, null, failureCode, failureMessage);
}
