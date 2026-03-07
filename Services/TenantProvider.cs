using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Models;

namespace SentinelMfgSuite.Web.Services;

public class TenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _http;
    private readonly UserManager<ApplicationUser> _users;

    public TenantProvider(IHttpContextAccessor http, UserManager<ApplicationUser> users)
    {
        _http = http;
        _users = users;
    }

    public async Task<Guid> GetTenantIdAsync(CancellationToken ct = default)
    {
        var ctx = _http.HttpContext ?? throw new InvalidOperationException("No HttpContext.");
        var userId = _users.GetUserId(ctx.User) ?? throw new InvalidOperationException("Not authenticated.");

        var user = await _users.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            throw new InvalidOperationException("Authenticated user not found in database.");

        if (user.TenantId is null)
            throw new InvalidOperationException("User has no tenant. Payment/provisioning incomplete.");

        return user.TenantId.Value;
    }
}

