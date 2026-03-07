namespace SentinelMfgSuite.Web.Models.Tenants;

public class TenantSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = default!; // ✅ add this

    public string Tier { get; set; } = "Tier1";
    public string BillingCadence { get; set; } = "Monthly";

    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
}
