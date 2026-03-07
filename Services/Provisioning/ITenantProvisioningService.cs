namespace SentinelMfgSuite.Web.Services.Provisioning;

public interface ITenantProvisioningService
{
    Task<Guid> ProvisionTenantForUserAsync(
        string userId,
        string tenantName,
        string tier = "Tier1",
        string billingCadence = "Monthly",
        string? stripeCustomerId = null,
        string? stripeSubscriptionId = null,
        CancellationToken ct = default);
}


