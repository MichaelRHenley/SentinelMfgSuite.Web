namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationSendService
{
    Task<AssessmentInvitationSendResult> SendInvitationAsync(Guid assessmentId, CancellationToken ct = default);
}
