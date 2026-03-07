using Microsoft.AspNetCore.Identity;

namespace SentinelMfgSuite.Web.Models;

public class ApplicationUser : IdentityUser
{
    public Guid? TenantId { get; set; }
    public UserProvisioningState ProvisioningState { get; set; } = UserProvisioningState.Registered;

    public string? DisplayName { get; set; }
    public string? CompanyName { get; set; }

}

public enum UserProvisioningState
{
    Registered = 0,
    PaymentPending = 1,
    TenantProvisioned = 2,
    Active = 3
}
