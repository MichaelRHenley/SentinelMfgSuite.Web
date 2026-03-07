using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Services.Provisioning;


namespace SentinelMfgSuite.Web.Controllers;

[Authorize]
public class SubscribeController : Controller
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITenantProvisioningService _provisioning;
    private readonly SignInManager<ApplicationUser> _signIn;

    public SubscribeController(
        UserManager<ApplicationUser> users,
        ITenantProvisioningService provisioning,
        SignInManager<ApplicationUser> signIn)
    {
        _users = users;
        _provisioning = provisioning;
        _signIn = signIn;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User);

        // Already provisioned? go to onboarding/app
        if (user?.TenantId != null)
            return RedirectToAction("Start", "Onboarding");

        return View();
    }

   
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTenant(string? tenantName, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return Challenge();

        // Already provisioned
        if (user.TenantId.HasValue && user.TenantId.Value != Guid.Empty)
            return RedirectToAction("Start", "Onboarding");

        // Provision (service handles tenantName defaults)
        await _provisioning.ProvisionTenantForUserAsync(
            user.Id,
            tenantName ?? "",
            ct: ct);

        // Refresh cookie so tenant_id / provisioning_state claims appear immediately
        user = await _users.GetUserAsync(User);
        if (user is not null)
            await _signIn.RefreshSignInAsync(user);

        // Verify provisioning succeeded; otherwise show the Subscribe page with an error
        if (user?.TenantId.HasValue == true && user.TenantId.Value != Guid.Empty)
            return RedirectToAction("Start", "Onboarding");

        ModelState.AddModelError("", "Tenant provisioning failed. Please try again.");
        return View("Index");
    }

}
