namespace SentinelMfgSuite.Web.Models.Tenants;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum TenantStatus
{
    Pending = 0,
    Active = 1,
    Suspended = 2
}
