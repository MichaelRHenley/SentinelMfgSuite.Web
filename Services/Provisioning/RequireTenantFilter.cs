using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SentinelMfgSuite.Web.Services;

namespace SentinelMfgSuite.Web.Services.Provisioning;

public sealed class RequireTenantFilter : IAsyncActionFilter
{
    private readonly ITenantProvider _tenantProvider;

    public RequireTenantFilter(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Only enforce when the endpoint is authenticated (otherwise let auth redirect)
        if (context.HttpContext.User?.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        try
        {
            _ = await _tenantProvider.GetTenantIdAsync(context.HttpContext.RequestAborted);
            await next();
        }
        catch (InvalidOperationException)
        {
            // No tenant provisioned -> push user into Subscribe flow
            context.Result = new RedirectToActionResult("Index", "Subscribe", routeValues: null);
        }
    }
}

