namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentInvitationDeliveryService
{
    Task<AssessmentInvitationDeliveryResult> DeliverAsync(AssessmentInvitationDeliveryRequest request, CancellationToken ct = default);
}
