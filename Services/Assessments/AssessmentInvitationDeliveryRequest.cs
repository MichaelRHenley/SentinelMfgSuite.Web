namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentInvitationDeliveryRequest
{
    public AssessmentInvitationDeliveryRequest(
        Guid assessmentId,
        Guid invitationId,
        string recipientEmail,
        string rawInvitationToken,
        DateTime expiresUtc)
    {
        if (assessmentId == Guid.Empty)
        {
            throw new ArgumentException("Assessment id is required.", nameof(assessmentId));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id is required.", nameof(invitationId));
        }

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            throw new ArgumentException("Recipient email is required.", nameof(recipientEmail));
        }

        if (string.IsNullOrWhiteSpace(rawInvitationToken))
        {
            throw new ArgumentException("Raw invitation token is required.", nameof(rawInvitationToken));
        }

        if (expiresUtc == default)
        {
            throw new ArgumentException("Expiration timestamp is required.", nameof(expiresUtc));
        }

        AssessmentId = assessmentId;
        InvitationId = invitationId;
        RecipientEmail = recipientEmail.Trim();
        RawInvitationToken = rawInvitationToken.Trim();
        ExpiresUtc = expiresUtc;
    }

    public Guid AssessmentId { get; }

    public Guid InvitationId { get; }

    public string RecipientEmail { get; }

    public string RawInvitationToken { get; }

    public DateTime ExpiresUtc { get; }
}
