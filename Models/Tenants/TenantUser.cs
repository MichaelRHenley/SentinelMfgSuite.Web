namespace SentinelMfgSuite.Web.Models.Tenants;

public class TenantUser
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = default!;

    public Guid UserId { get; set; } // ties to your Identity user key type
    public string Email { get; set; } = default!; // optional but handy

    public bool IsOwner { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

