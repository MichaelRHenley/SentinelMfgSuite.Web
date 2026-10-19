namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationReplacementEligibilityService
{
    Task<AssessmentInvitationReplacementEligibilityResult> EvaluateAsync(Guid assessmentId, Guid invitationId, CancellationToken ct = default);
}
