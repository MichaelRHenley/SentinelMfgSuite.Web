using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SentinelMfgSuite.Web.Services;
using SentinelMfgSuite.Web.Services.Provisioning;


namespace SentinelMfgSuite.Web.Controllers;

[Authorize]
[ServiceFilter(typeof(RequireTenantFilter))]
public abstract class TenantController : Controller
{
    protected readonly ITenantProvider TenantProvider;

    protected TenantController(ITenantProvider tenantProvider)
        => TenantProvider = tenantProvider;

    protected Task<Guid> RequireTenantIdAsync(CancellationToken ct = default)
        => TenantProvider.GetTenantIdAsync(ct);
}
