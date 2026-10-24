namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed record AssessmentProvisioningRequest
{
    public AssessmentProvisioningRequest(
        AssessmentProvisioningInitiationSource initiationSource,
        string assessmentTitle,
        string siteName,
        string customerContactEmail,
        Guid? tenantId = null,
        string? ownerUserId = null,
        string? externalPurchaseReference = null)
    {
        if (string.IsNullOrWhiteSpace(assessmentTitle))
        {
            throw new ArgumentException("Assessment title is required.", nameof(assessmentTitle));
        }

        if (string.IsNullOrWhiteSpace(siteName))
        {
            throw new ArgumentException("Site name is required.", nameof(siteName));
        }

        if (string.IsNullOrWhiteSpace(customerContactEmail))
        {
            throw new ArgumentException("Customer contact email is required.", nameof(customerContactEmail));
        }

        InitiationSource = initiationSource;
        AssessmentTitle = assessmentTitle.Trim();
        SiteName = siteName.Trim();
        CustomerContactEmail = customerContactEmail.Trim();
        TenantId = tenantId;
        OwnerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? null : ownerUserId.Trim();
        ExternalPurchaseReference = string.IsNullOrWhiteSpace(externalPurchaseReference) ? null : externalPurchaseReference.Trim();
    }

    public AssessmentProvisioningInitiationSource InitiationSource { get; }

    public string AssessmentTitle { get; }

    public string SiteName { get; }

    public string CustomerContactEmail { get; }

    public Guid? TenantId { get; }

    public string? OwnerUserId { get; }

    public string? ExternalPurchaseReference { get; }

    public string? CustomerOrganization { get; init; }

    public string? BillingContactName { get; init; }

    public string? BillingContactEmail { get; init; }
}
