namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationReplacementSendService
{
    Task<AssessmentInvitationReplacementSendResult> ReplaceAndSendAsync(
        Guid assessmentId,
        Guid existingInvitationId,
        CancellationToken ct = default,
        Action<Guid, string>? onDevelopmentEmailSuppressed = null);
}
