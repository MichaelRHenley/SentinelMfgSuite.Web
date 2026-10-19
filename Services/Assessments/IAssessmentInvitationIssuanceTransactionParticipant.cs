namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationIssuanceTransactionParticipant
{
    Task<AssessmentInvitationIssuanceResult> IssueWithinExistingTransactionAsync(Guid assessmentId, TimeSpan expiresIn, CancellationToken ct = default);
}
