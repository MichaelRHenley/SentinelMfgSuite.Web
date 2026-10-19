namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentInvitationIssuanceResult(
    bool Succeeded,
    Guid? InvitationId,
    string? InvitationToken,
    DateTime? ExpiresUtc,
    string? FailureCode,
    string? FailureMessage)
{
    public static AssessmentInvitationIssuanceResult Success(Guid invitationId, string invitationToken, DateTime expiresUtc)
        => new(true, invitationId, invitationToken, expiresUtc, null, null);

    public static AssessmentInvitationIssuanceResult Failure(string failureCode, string failureMessage)
        => new(false, null, null, null, failureCode, failureMessage);
}
