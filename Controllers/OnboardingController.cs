using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Services;

namespace SentinelMfgSuite.Web.Controllers;

[Authorize]
public class OnboardingController : Controller
{
    private readonly ITenantProvider _tenantProvider;
    private readonly UserManager<ApplicationUser> _users;

    public OnboardingController(ITenantProvider tenantProvider, UserManager<ApplicationUser> users)
    {
        _tenantProvider = tenantProvider;
        _users = users;
    }

    [HttpGet]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        // If tenant isn't provisioned, go to Subscribe
        try
        {
            _ = await _tenantProvider.GetTenantIdAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return RedirectToAction("Index", "Subscribe");
        }

        // Mark user Active once they've crossed into onboarding
        var user = await _users.GetUserAsync(User);
        if (user is not null && user.ProvisioningState != UserProvisioningState.Active)
        {
            user.ProvisioningState = UserProvisioningState.Active;
            await _users.UpdateAsync(user);
        }

        // Next step will be the real wizard. For now go to App.
        return RedirectToAction("App", "Home"); // use your gated app landing
    }
}
