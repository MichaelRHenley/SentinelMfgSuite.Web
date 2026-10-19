namespace SentinelMfgSuite.Web.Services.Assessments;

public enum AssessmentInvitationReplacementSendOutcome
{
    Delivered,
    EmailSuppressed,
    DeliveryFailed,
    ReplacementFailed
}

public sealed record AssessmentInvitationReplacementSendResult(
    AssessmentInvitationReplacementSendOutcome Outcome,
    Guid? InvitationId,
    string? FailureCode,
    string? FailureMessage)
{
    public bool Succeeded => Outcome == AssessmentInvitationReplacementSendOutcome.Delivered;

    public static AssessmentInvitationReplacementSendResult Delivered(Guid invitationId)
        => new(AssessmentInvitationReplacementSendOutcome.Delivered, invitationId, null, null);

    public static AssessmentInvitationReplacementSendResult EmailSuppressed(Guid invitationId, string failureCode, string failureMessage)
        => new(AssessmentInvitationReplacementSendOutcome.EmailSuppressed, invitationId, failureCode, failureMessage);

    public static AssessmentInvitationReplacementSendResult DeliveryFailed(Guid invitationId, string failureCode, string failureMessage)
        => new(AssessmentInvitationReplacementSendOutcome.DeliveryFailed, invitationId, failureCode, failureMessage);

    public static AssessmentInvitationReplacementSendResult ReplacementFailed(string failureCode, string failureMessage)
        => new(AssessmentInvitationReplacementSendOutcome.ReplacementFailed, null, failureCode, failureMessage);
}
