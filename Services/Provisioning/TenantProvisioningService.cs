using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models;
using SentinelMfgSuite.Web.Models.Tenants;

namespace SentinelMfgSuite.Web.Services.Provisioning;

public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<TenantProvisioningService> _log;

    public TenantProvisioningService(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        ILogger<TenantProvisioningService> log)
    {
        _db = db;
        _users = users;
        _log = log;
    }

    public async Task<Guid> ProvisionTenantForUserAsync(
    string userId,
    string tenantName,
    string tier = "Tier1",
    string billingCadence = "Monthly",
    string? stripeCustomerId = null,
    string? stripeSubscriptionId = null,
    CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException($"User not found: {userId}");

        // Normalize tenantName here (Tier 1 safe default)
        tenantName = string.IsNullOrWhiteSpace(tenantName)
            ? (user.CompanyName ?? user.Email ?? "New Tenant")
            : tenantName.Trim();

        // Re-check INSIDE the transaction
        if (user.TenantId.HasValue && user.TenantId.Value != Guid.Empty)
        {
            await tx.CommitAsync(ct);
            return user.TenantId.Value;
        }

        try
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = tenantName,
                Status = TenantStatus.Active,
                CreatedUtc = DateTime.UtcNow
            };
            _db.Tenants.Add(tenant);

            _db.TenantSubscriptions.Add(new TenantSubscription
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Tier = tier,
                BillingCadence = billingCadence,
                StripeCustomerId = stripeCustomerId,
                StripeSubscriptionId = stripeSubscriptionId,
                IsActive = true,
                StartedUtc = DateTime.UtcNow
            });

            _db.TenantUserRoles.AddRange(
                new TenantUserRole { TenantId = tenant.Id, UserId = user.Id, Role = TenantRole.TenantOwner },
                new TenantUserRole { TenantId = tenant.Id, UserId = user.Id, Role = TenantRole.TenantAdmin }
            );

            user.TenantId = tenant.Id;
            user.ProvisioningState = UserProvisioningState.TenantProvisioned;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("Provisioned tenant {TenantId} for user {UserId}", tenant.Id, user.Id);
            return tenant.Id;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

}



