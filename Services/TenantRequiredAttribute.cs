using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Identity;
using SentinelMfgSuite.Web.Models;

namespace SentinelMfgSuite.Web.Services;

public class TenantRequiredAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userMgr = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userMgr.GetUserAsync(context.HttpContext.User);

        if (user is null)
        {
            context.Result = new RedirectToActionResult("Login", "Account", null);
            return;
        }

        if (user.TenantId is null)
        {
            context.Result = new RedirectToActionResult("Index", "Subscribe", null);
            return;
        }

        await next();
    }
}

