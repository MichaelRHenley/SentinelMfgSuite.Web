using Microsoft.EntityFrameworkCore;
using Xunit;
using SentinelMfgSuite.Web.Data;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Payments;
using SentinelMfgSuite.Web.Tests.Infrastructure;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentOrderProvisioningTests
{
    private static AssessmentPricingOptions Pricing(bool enforce = true, decimal price = 2500.00m) => new()
    {
        BasePrice = price,
        Currency = "CAD",
        TaxTreatment = AssessmentTaxTreatment.None,
        TaxRate = 0m,
        EnforcePayment = enforce
    };

    private static async Task<(PostgresIntegrationTestFixture pg, AssessmentProvisioningService svc, string title)> Start(AssessmentPricingOptions p)
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        var db = pg.CreateDbContext();
        var svc = new AssessmentProvisioningService(db, null, null, Options.Create(p));
        return (pg, svc, "Prov-" + Guid.NewGuid().ToString("N"));
    }

    private static AssessmentProvisioningRequest Request(string title, AssessmentProvisioningInitiationSource src = AssessmentProvisioningInitiationSource.InternalDraftAdmin, string? billingEmail = "billing@example.com")
        => new(src, title, "Site A", "contact@example.com", ownerUserId: "admin-1")
        {
            CustomerOrganization = "Acme Mfg",
            BillingContactName = "Pat Buyer",
            BillingContactEmail = billingEmail
        };

    private static async Task Cleanup(PostgresIntegrationTestFixture pg, string title)
    {
        await using var db = pg.CreateDbContext();
        var ids = await db.Set<Assessment>().Where(a => a.Title == title).Select(a => a.Id).ToListAsync();
        var orderIds = await db.Set<AssessmentOrder>().Where(o => ids.Contains(o.AssessmentId)).Select(o => o.Id).ToListAsync();
        await db.Set<AssessmentPaymentEvent>().Where(e => orderIds.Contains(e.OrderId)).ExecuteDeleteAsync();
        await db.Set<AssessmentOrder>().Where(o => ids.Contains(o.AssessmentId)).ExecuteDeleteAsync();
        await db.Set<AssessmentInvitation>().Where(i => ids.Contains(i.AssessmentId)).ExecuteDeleteAsync();
        await db.Set<Assessment>().Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync();
    }

    [Theory]
    [InlineData(AssessmentProvisioningInitiationSource.InternalDraftAdmin)]
    [InlineData(AssessmentProvisioningInitiationSource.ManualAdmin)]
    public async Task Required_Provisioning_Creates_Pending_Order_With_Snapshot_And_No_Invitation(AssessmentProvisioningInitiationSource src)
    {
        var (pg, svc, title) = await Start(Pricing());
        try
        {
            var r = await svc.ProvisionAsync(Request(title, src));
            Assert.True(r.Succeeded, r.FailureMessage);
            Assert.Null(r.InvitationId);

            await using var db = pg.CreateDbContext();
            var a = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == r.AssessmentId);
            Assert.Equal(AssessmentPaymentPolicy.Required, a.PaymentPolicy);
            Assert.Equal(AssessmentStatus.Draft, a.Status);
            var o = await db.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.AssessmentId == a.Id);
            Assert.Equal(AssessmentPaymentStatus.Pending, o.PaymentStatus);
            Assert.Equal(2500.00m, o.UnitPrice);
            Assert.Equal(0m, o.TaxAmount);
            Assert.Equal(2500.00m, o.TotalAmount);
            Assert.Equal("CAD", o.Currency);
            Assert.Equal(AssessmentTaxTreatment.None, o.TaxTreatment);
            Assert.Equal("Acme Mfg", o.CustomerOrganization);
            Assert.Equal("Pat Buyer", o.BillingContactName);
            Assert.Equal("billing@example.com", o.BillingContactEmail);
            Assert.False(await db.Set<AssessmentInvitation>().AnyAsync(i => i.AssessmentId == a.Id));
            Assert.Single(await db.Set<AssessmentPaymentEvent>().Where(e => e.OrderId == o.Id).ToListAsync());
        }
        finally { await Cleanup(pg, title); }
    }

    [Fact]
    public async Task Order_Creation_Failure_Rolls_Back_Assessment()
    {
        var (pg, svc, title) = await Start(Pricing());
        try
        {
            // TaxLabel longer than the 50-char column passes request validation but fails at the database when the order is saved.
            var p = Pricing();
            p.TaxTreatment = AssessmentTaxTreatment.Exclusive;
            p.TaxRate = 0.05m;
            p.TaxLabel = new string('x', 80);
            var failing = new AssessmentProvisioningService(pg.CreateDbContext(), null, null, Options.Create(p));

            await Assert.ThrowsAnyAsync<Exception>(() => failing.ProvisionAsync(Request(title)));

            await using var db = pg.CreateDbContext();
            Assert.False(await db.Set<Assessment>().AnyAsync(a => a.Title == title));
        }
        finally { await Cleanup(pg, title); }
    }

    [Theory]
    [InlineData(null, "Pat", "b@example.com")]
    [InlineData("Acme", " ", "b@example.com")]
    [InlineData("Acme", "Pat", null)]
    [InlineData("Acme", "Pat", "not-an-email")]
    public async Task Invalid_Billing_Prevents_Assessment_And_Order_Creation(string? org, string? name, string? email)
    {
        foreach (var src in new[] { AssessmentProvisioningInitiationSource.InternalDraftAdmin, AssessmentProvisioningInitiationSource.ManualAdmin })
        {
            var (pg, svc, title) = await Start(Pricing());
            try
            {
                var req = new AssessmentProvisioningRequest(src, title, "Site A", "contact@example.com", ownerUserId: "admin-1")
                { CustomerOrganization = org, BillingContactName = name, BillingContactEmail = email };
                var r = await svc.ProvisionAsync(req);
                Assert.False(r.Succeeded);
                Assert.Equal("BillingInformationInvalid", r.FailureCode);

                await using var db = pg.CreateDbContext();
                Assert.False(await db.Set<Assessment>().AnyAsync(a => a.Title == title));
            }
            finally { await Cleanup(pg, title); }
        }
    }

    [Fact]
    public async Task Legacy_Policy_Does_Not_Require_Billing_Information()
    {
        var (pg, svc, title) = await Start(Pricing(enforce: false, price: 0m));
        try
        {
            var req = new AssessmentProvisioningRequest(AssessmentProvisioningInitiationSource.InternalDraftAdmin, title, "Site A", "contact@example.com");
            var r = await svc.ProvisionAsync(req);
            Assert.True(r.Succeeded, r.FailureMessage);
        }
        finally { await Cleanup(pg, title); }
    }
    [Fact]
    public async Task Invalid_Pricing_Fails_Without_Creating_Anything_Or_Defaulting()
    {
        var (pg, svc, title) = await Start(Pricing(price: 0m));
        try
        {
            var r = await svc.ProvisionAsync(Request(title));
            Assert.False(r.Succeeded);
            Assert.Equal("PricingNotConfigured", r.FailureCode);

            await using var db = pg.CreateDbContext();
            Assert.False(await db.Set<Assessment>().AnyAsync(a => a.Title == title));
        }
        finally { await Cleanup(pg, title); }
    }

    [Fact]
    public async Task Duplicate_Order_For_Same_Assessment_Is_Rejected()
    {
        var (pg, svc, title) = await Start(Pricing());
        try
        {
            var r = await svc.ProvisionAsync(Request(title));
            await using var db = pg.CreateDbContext();
            var dup = await new AssessmentOrderService(db, Options.Create(Pricing()))
                .CreateOrderAsync(r.AssessmentId!.Value, "Acme", "Pat", "p@example.com", AssessmentPaymentMethod.BankTransfer, "admin-1");
            Assert.False(dup.Succeeded);
            Assert.Equal(1, await db.Set<AssessmentOrder>().CountAsync(o => o.AssessmentId == r.AssessmentId));
        }
        finally { await Cleanup(pg, title); }
    }

    [Fact]
    public async Task Admin_Workflow_Pending_Blocks_Invitation_And_Verified_Bank_Transfer_Permits_It()
    {
        var (pg, svc, title) = await Start(Pricing());
        try
        {
            var r = await svc.ProvisionAsync(Request(title));
            var id = r.AssessmentId!.Value;

            await using (var db = pg.CreateDbContext())
            {
                var blocked = await new AssessmentInvitationIssuanceService(db).IssueAsync(id, TimeSpan.FromDays(7));
                Assert.False(blocked.Succeeded);
            }

            await using (var db = pg.CreateDbContext())
            {
                var orderId = await db.Set<AssessmentOrder>().Where(o => o.AssessmentId == id).Select(o => o.Id).SingleAsync();
                var orders = new AssessmentOrderService(db, Options.Create(Pricing()));
                Assert.False((await orders.VerifyBankTransferAsync(orderId, "admin-1", " ")).Succeeded);
                Assert.True((await orders.VerifyBankTransferAsync(orderId, "admin-1", "EFT-12345")).Succeeded);
            }

            await using (var db = pg.CreateDbContext())
            {
                var ok = await new AssessmentInvitationIssuanceService(db).IssueAsync(id, TimeSpan.FromDays(7));
                Assert.True(ok.Succeeded, ok.FailureMessage);
            }
        }
        finally { await Cleanup(pg, title); }
    }

    [Fact]
    public async Task Legacy_Policy_Provisioning_Creates_No_Order()
    {
        var (pg, svc, title) = await Start(Pricing(enforce: false, price: 0m));
        try
        {
            var r = await svc.ProvisionAsync(Request(title));
            Assert.True(r.Succeeded, r.FailureMessage);

            await using var db = pg.CreateDbContext();
            var a = await db.Set<Assessment>().AsNoTracking().SingleAsync(x => x.Id == r.AssessmentId);
            Assert.Equal(AssessmentPaymentPolicy.LegacyExempt, a.PaymentPolicy);
            Assert.False(await db.Set<AssessmentOrder>().AnyAsync(o => o.AssessmentId == a.Id));
        }
        finally { await Cleanup(pg, title); }
    }
}
