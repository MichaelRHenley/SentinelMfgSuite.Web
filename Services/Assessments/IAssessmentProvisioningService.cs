namespace SentinelMfgSuite.Web.Services.Assessments;

public interface IAssessmentProvisioningService
{
    Task<AssessmentProvisioningResult> ProvisionAsync(AssessmentProvisioningRequest request, CancellationToken ct = default);
}
