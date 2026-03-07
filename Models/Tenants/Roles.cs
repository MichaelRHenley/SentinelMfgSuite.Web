namespace SentinelMfgSuite.Web.Models.Tenants;

public class TenantUserRole
{
    public Guid TenantId { get; set; }
    public string UserId { get; set; } = default!;  // ✅ string, not Guid
    public TenantRole Role { get; set; }
}

public enum TenantRole
{
    TenantOwner = 0,
    TenantAdmin = 1,
    Maintenance = 2,
    Operator = 3,
    Supervisor = 4,
    Safety = 5,
    Quality = 6
}

