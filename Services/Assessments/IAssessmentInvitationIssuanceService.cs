namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationIssuanceService
{
    Task<AssessmentInvitationIssuanceResult> IssueAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default);

    Task<AssessmentInvitationReplacementIssuanceResult> ReplaceAsync(Guid assessmentId, Guid existingInvitationId, TimeSpan expiresIn, CancellationToken ct = default);
}
