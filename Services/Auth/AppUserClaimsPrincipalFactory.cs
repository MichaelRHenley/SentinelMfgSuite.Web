using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Models;

namespace SentinelMfgSuite.Web.Services.Auth;

public class AppUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public AppUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.TenantId.HasValue && user.TenantId.Value != Guid.Empty)
            identity.AddClaim(new Claim("tenant_id", user.TenantId.Value.ToString()));

        identity.AddClaim(new Claim("provisioning_state", ((int)user.ProvisioningState).ToString()));

        return identity;
    }
}

