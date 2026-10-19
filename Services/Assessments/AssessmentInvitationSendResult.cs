namespace SentinelMfgSuite.Web.Services.Assessments;

public enum AssessmentInvitationSendOutcome
{
    Delivered,
    EmailSuppressed,
    DeliveryFailed,
    IssuanceFailed
}

public sealed record AssessmentInvitationSendResult(
    AssessmentInvitationSendOutcome Outcome,
    Guid? InvitationId,
    string? FailureCode,
    string? FailureMessage)
{
    public bool Succeeded => Outcome == AssessmentInvitationSendOutcome.Delivered;

    public static AssessmentInvitationSendResult Delivered(Guid invitationId)
        => new(AssessmentInvitationSendOutcome.Delivered, invitationId, null, null);

    public static AssessmentInvitationSendResult EmailSuppressed(Guid invitationId, string failureCode, string failureMessage)
        => new(AssessmentInvitationSendOutcome.EmailSuppressed, invitationId, failureCode, failureMessage);

    public static AssessmentInvitationSendResult DeliveryFailed(Guid invitationId, string failureCode, string failureMessage)
        => new(AssessmentInvitationSendOutcome.DeliveryFailed, invitationId, failureCode, failureMessage);

    public static AssessmentInvitationSendResult IssuanceFailed(string failureCode, string failureMessage)
        => new(AssessmentInvitationSendOutcome.IssuanceFailed, null, failureCode, failureMessage);
}
