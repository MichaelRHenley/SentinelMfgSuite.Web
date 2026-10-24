using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Payments;
using SentinelMfgSuite.Web.Tests.Infrastructure;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "PostgresIntegration")]
public class AssessmentOrderPaymentTests
{
    private static AssessmentPricingOptions Pricing(decimal price = 1234.50m, AssessmentTaxTreatment tax = AssessmentTaxTreatment.None, decimal rate = 0m)
        => new() { BasePrice = price, TaxTreatment = tax, TaxRate = rate, TaxLabel = tax == AssessmentTaxTreatment.Exclusive ? "GST" : null };

    private static AssessmentOrderService Orders(ApplicationDbContext db, AssessmentPricingOptions? p = null)
        => new(db, Options.Create(p ?? Pricing()));

    private static async Task<Guid> SeedAssessment(ApplicationDbContext db, AssessmentPaymentPolicy policy)
    {
        var now = DateTime.UtcNow;
        var a = new Assessment
        {
            Id = Guid.NewGuid(), Title = "Pay test", SiteName = "Site", ContactEmail = $"c{Guid.NewGuid():N}@example.com",
            Status = AssessmentStatus.Draft, CreatedUtc = now, UpdatedUtc = now, PaymentPolicy = policy
        };
        db.Set<Assessment>().Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    private static async Task Cleanup(PostgresIntegrationTestFixture pg, Guid assessmentId)
    {
        await using var db = pg.CreateDbContext();
        var orderIds = await db.Set<AssessmentOrder>().Where(o => o.AssessmentId == assessmentId).Select(o => o.Id).ToListAsync();
        await db.Set<AssessmentPaymentEvent>().Where(e => orderIds.Contains(e.OrderId)).ExecuteDeleteAsync();
        await db.Set<AssessmentOrder>().Where(o => o.AssessmentId == assessmentId).ExecuteDeleteAsync();
        await db.Set<AssessmentInvitation>().Where(i => i.AssessmentId == assessmentId).ExecuteDeleteAsync();
        await db.Set<Assessment>().Where(a => a.Id == assessmentId).ExecuteDeleteAsync();
    }

    private static async Task<(PostgresIntegrationTestFixture pg, Guid assessmentId)> Start(AssessmentPaymentPolicy policy = AssessmentPaymentPolicy.Required)
    {
        var pg = new PostgresIntegrationTestFixture();
        await pg.InitializeAsync();
        await using var db = pg.CreateDbContext();
        return (pg, await SeedAssessment(db, policy));
    }

    private static async Task<Guid> NewOrder(PostgresIntegrationTestFixture pg, Guid assessmentId, AssessmentPaymentMethod method, AssessmentPricingOptions? p = null)
    {
        await using var db = pg.CreateDbContext();
        var r = await Orders(db, p).CreateOrderAsync(assessmentId, "Acme Mfg", "Pat Buyer", "ap@example.com", method, "admin-1");
        Assert.True(r.Succeeded, r.FailureMessage);
        return r.OrderId!.Value;
    }

    private static async Task<AssessmentInvitationIssuanceResult> Issue(PostgresIntegrationTestFixture pg, Guid assessmentId)
    {
        await using var db = pg.CreateDbContext();
        return await new AssessmentInvitationIssuanceService(db).IssueAsync(assessmentId, TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task Order_Snapshot_Uses_Configured_CAD_Price_And_No_Tax_By_Default()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using var db = pg.CreateDbContext();
            var o = await db.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Equal("CAD", o.Currency);
            Assert.Equal(1234.50m, o.UnitPrice);
            Assert.Equal(0m, o.TaxAmount);
            Assert.Equal(1234.50m, o.TotalAmount);
            Assert.Equal(AssessmentTaxTreatment.None, o.TaxTreatment);
            Assert.Equal(AssessmentPaymentStatus.Pending, o.PaymentStatus);
            Assert.Single(await db.Set<AssessmentPaymentEvent>().Where(e => e.OrderId == orderId).ToListAsync());
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Tax_Is_Added_Only_When_Explicitly_Configured_And_Snapshot_Is_Immutable()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer, Pricing(1000m, AssessmentTaxTreatment.Exclusive, 0.05m));
            await using var db = pg.CreateDbContext();
            var o = await db.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Equal(50.00m, o.TaxAmount);
            Assert.Equal(1050.00m, o.TotalAmount);
            Assert.Equal("GST", o.TaxLabel);

            // A later pricing change cannot alter the existing order (a second order is rejected, the snapshot is unchanged).
            var second = await Orders(db, Pricing(9999m)).CreateOrderAsync(id, "Acme", "Pat", "ap@example.com", AssessmentPaymentMethod.BankTransfer, "admin-1");
            Assert.False(second.Succeeded);
            await using var verify = pg.CreateDbContext();
            Assert.Equal(1050.00m, (await verify.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.Id == orderId)).TotalAmount);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Unconfigured_Or_Invalid_Pricing_Is_Rejected()
    {
        var (pg, id) = await Start();
        try
        {
            await using var db = pg.CreateDbContext();
            Assert.Equal("PricingNotConfigured", (await Orders(db, Pricing(0m)).CreateOrderAsync(id, "A", "B", "b@example.com", AssessmentPaymentMethod.BankTransfer, "x")).FailureCode);
            Assert.Equal("PricingNotConfigured", (await Orders(db, Pricing(100m, AssessmentTaxTreatment.Exclusive, 0m)).CreateOrderAsync(id, "A", "B", "b@example.com", AssessmentPaymentMethod.BankTransfer, "x")).FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Theory]
    [InlineData(AssessmentPaymentMethod.BankTransfer)]
    [InlineData(AssessmentPaymentMethod.PurchaseOrder)]
    [InlineData(AssessmentPaymentMethod.OnlineProvider)]
    public async Task Pending_Order_Does_Not_Authorize_Invitation(AssessmentPaymentMethod method)
    {
        var (pg, id) = await Start();
        try
        {
            await NewOrder(pg, id, method);
            var result = await Issue(pg, id);
            Assert.False(result.Succeeded);
            Assert.Equal("PaymentRequired", result.FailureCode);
            await using var db = pg.CreateDbContext();
            Assert.Equal(AssessmentStatus.Draft, (await db.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == id)).Status);
            Assert.Empty(await db.Set<AssessmentInvitation>().Where(i => i.AssessmentId == id).ToListAsync());
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Assessment_Without_Order_Cannot_Be_Invited()
    {
        var (pg, id) = await Start();
        try
        {
            Assert.Equal("PaymentRequired", (await Issue(pg, id)).FailureCode);
            await using var db = pg.CreateDbContext();
            Assert.Equal("NoOrder", (await new InvitationPaymentAuthorizer(db).EvaluateAsync(id)).Reason);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Purchase_Order_Cannot_Be_Manually_Verified_And_Never_Authorizes()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.PurchaseOrder);
            await using var db = pg.CreateDbContext();
            var r = await Orders(db).VerifyBankTransferAsync(orderId, "admin-1", "PO-1");
            Assert.Equal("MethodNotVerifiable", r.FailureCode);
            Assert.Equal("PaymentRequired", (await Issue(pg, id)).FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Bank_Transfer_Verification_Requires_Admin_And_Reference_Then_Authorizes_Invitation()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using (var db = pg.CreateDbContext())
            {
                var svc = Orders(db);
                Assert.Equal("Unauthorized", (await svc.VerifyBankTransferAsync(orderId, " ", "REF")).FailureCode);
                Assert.Equal("ReferenceRequired", (await svc.VerifyBankTransferAsync(orderId, "admin-1", " ")).FailureCode);
            }
            Assert.Equal("PaymentRequired", (await Issue(pg, id)).FailureCode);

            await using (var db = pg.CreateDbContext())
            {
                Assert.True((await Orders(db).VerifyBankTransferAsync(orderId, "admin-1", "WIRE-42")).Succeeded);
            }

            await using var verify = pg.CreateDbContext();
            var o = await verify.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.Equal(AssessmentPaymentStatus.Paid, o.PaymentStatus);
            Assert.NotNull(o.VerifiedPaymentUtc);
            Assert.Equal("admin-1", o.VerifiedBy);
            Assert.Equal("WIRE-42", o.PaymentReference);
            var ev = await verify.Set<AssessmentPaymentEvent>().AsNoTracking().SingleAsync(e => e.OrderId == orderId && e.EventType == AssessmentPaymentEventType.PaymentVerified);
            Assert.Equal("admin-1", ev.ActorId);
            Assert.Equal(AssessmentPaymentActorType.Administrator, ev.ActorType);

            Assert.True((await Issue(pg, id)).Succeeded);
            Assert.True((await new InvitationPaymentAuthorizer(verify).EvaluateAsync(id)).Allowed);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Second_Verification_Is_Rejected_And_Does_Not_Duplicate_Events()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using var db = pg.CreateDbContext();
            Assert.True((await Orders(db).VerifyBankTransferAsync(orderId, "a1", "R1")).Succeeded);
            Assert.Equal("InvalidTransition", (await Orders(db).VerifyBankTransferAsync(orderId, "a2", "R2")).FailureCode);
            Assert.Equal(1, await db.Set<AssessmentPaymentEvent>().CountAsync(e => e.OrderId == orderId && e.EventType == AssessmentPaymentEventType.PaymentVerified));
        }
        finally { await Cleanup(pg, id); }
    }

    [Theory]
    [InlineData(AssessmentPaymentEventType.PaymentFailed, AssessmentPaymentStatus.Failed)]
    public async Task Failed_And_Cancelled_Orders_Do_Not_Authorize(AssessmentPaymentEventType type, AssessmentPaymentStatus expected)
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.OnlineProvider);
            await using var db = pg.CreateDbContext();
            Assert.True((await Orders(db).ApplyProviderEventAsync(orderId, new ProviderPaymentEvent("testpay", "evt_1", type, "pay_1", null))).Succeeded);
            Assert.Equal(expected, (await db.Set<AssessmentOrder>().AsNoTracking().SingleAsync(o => o.Id == orderId)).PaymentStatus);
            Assert.Equal("PaymentRequired", (await Issue(pg, id)).FailureCode);

            Assert.True((await Orders(db).CancelAsync(orderId, "admin-1", "customer withdrew")).Succeeded);
            Assert.Equal("PaymentRequired", (await Issue(pg, id)).FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Duplicate_Provider_Event_Does_Not_Create_Duplicate_Transition()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.OnlineProvider);
            var evt = new ProviderPaymentEvent("testpay", "evt_dup", AssessmentPaymentEventType.PaymentVerified, "pay_1", "amount-matched");
            await using var db = pg.CreateDbContext();
            Assert.True((await Orders(db).ApplyProviderEventAsync(orderId, evt)).Succeeded);
            Assert.True((await Orders(db).ApplyProviderEventAsync(orderId, evt)).Succeeded);
            await using var db2 = pg.CreateDbContext();
            Assert.True((await Orders(db2).ApplyProviderEventAsync(orderId, evt)).Succeeded);
            Assert.Equal(1, await db2.Set<AssessmentPaymentEvent>().CountAsync(e => e.OrderId == orderId && e.ProviderEventId == "evt_dup"));
            Assert.Equal(AssessmentPaymentStatus.Paid, (await db2.Set<AssessmentOrder>().AsNoTracking().SingleAsync(o => o.Id == orderId)).PaymentStatus);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Provider_Events_Cannot_Verify_Bank_Transfer_Orders()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using var db = pg.CreateDbContext();
            var r = await Orders(db).ApplyProviderEventAsync(orderId, new ProviderPaymentEvent("testpay", "evt_x", AssessmentPaymentEventType.PaymentVerified, null, null));
            Assert.Equal("MethodMismatch", r.FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Legacy_Assessment_Remains_Invitable_Without_An_Order()
    {
        var (pg, id) = await Start(AssessmentPaymentPolicy.LegacyExempt);
        try
        {
            Assert.True((await Issue(pg, id)).Succeeded);
            await using var db = pg.CreateDbContext();
            Assert.Equal("LegacyExempt", (await new InvitationPaymentAuthorizer(db).EvaluateAsync(id)).Reason);
            Assert.Equal("LegacyAssessment", (await Orders(db).CreateOrderAsync(id, "A", "B", "b@example.com", AssessmentPaymentMethod.BankTransfer, "x")).FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Theory]
    [InlineData("refund")]
    [InlineData("dispute")]
    public async Task Refund_Or_Dispute_Flags_Policy_Review_Blocks_New_Invitations_And_Keeps_Issued_One(string kind)
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using var db = pg.CreateDbContext();
            await Orders(db).VerifyBankTransferAsync(orderId, "admin-1", "WIRE-1");
            var issued = await Issue(pg, id);
            Assert.True(issued.Succeeded);

            var r = kind == "refund"
                ? await Orders(db).RecordRefundAsync(orderId, "admin-1", "RF-1")
                : await Orders(db).RecordDisputeAsync(orderId, "admin-1", "DP-1");
            Assert.True(r.Succeeded);

            await using var verify = pg.CreateDbContext();
            var o = await verify.Set<AssessmentOrder>().AsNoTracking().SingleAsync(x => x.Id == orderId);
            Assert.True(o.PolicyReviewRequired);
            Assert.Equal(kind == "refund" ? AssessmentPaymentStatus.Refunded : AssessmentPaymentStatus.Disputed, o.PaymentStatus);

            // The already-issued invitation is untouched (not silently revoked).
            var inv = await verify.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == issued.InvitationId);
            Assert.Null(inv.RevokedUtc);

            // Replacement of an undelivered invitation (a "new" invitation) is blocked until payment is verified again.
            await verify.Set<AssessmentInvitation>().Where(i => i.Id == issued.InvitationId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeliveryStatus, AssessmentInvitationDeliveryStatus.Failed));
            var replace = await new AssessmentInvitationIssuanceService(verify).ReplaceAsync(id, issued.InvitationId!.Value, TimeSpan.FromDays(7));
            Assert.Equal("PaymentRequired", replace.FailureCode);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Delivery_Claim_Is_Refused_When_Payment_Is_No_Longer_Verified()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            await using var db = pg.CreateDbContext();
            await Orders(db).VerifyBankTransferAsync(orderId, "admin-1", "WIRE-1");
            var issued = await Issue(pg, id);
            Assert.True(issued.Succeeded);
            await Orders(db).RecordDisputeAsync(orderId, "admin-1", "DP-1");

            var sender = new CapturingSender();
            var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppUrls:Base"] = "https://example.test" }).Build();
            var email = (await db.Set<AssessmentInvitation>().AsNoTracking().SingleAsync(i => i.Id == issued.InvitationId)).Email;
            await using var ddb = pg.CreateDbContext();
            var result = await new AssessmentInvitationDeliveryService(ddb, sender, cfg).DeliverAsync(
                new AssessmentInvitationDeliveryRequest(id, issued.InvitationId!.Value, email, issued.InvitationToken!, issued.ExpiresUtc!.Value));
            Assert.False(result.Succeeded);
            Assert.Equal(0, sender.Count);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Concurrent_Verification_Records_Exactly_One_Transition()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            var tasks = Enumerable.Range(0, 6).Select(async n =>
            {
                await using var db = pg.CreateDbContext();
                return await Orders(db).VerifyBankTransferAsync(orderId, $"admin-{n}", $"REF-{n}");
            }).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(1, results.Count(r => r.Succeeded));
            await using var verify = pg.CreateDbContext();
            Assert.Equal(1, await verify.Set<AssessmentPaymentEvent>().CountAsync(e => e.OrderId == orderId && e.EventType == AssessmentPaymentEventType.PaymentVerified));
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Concurrent_Verification_And_Issuance_Never_Issues_Without_Verified_Payment()
    {
        var (pg, id) = await Start();
        try
        {
            var orderId = await NewOrder(pg, id, AssessmentPaymentMethod.BankTransfer);
            var verify = Task.Run(async () =>
            {
                await using var db = pg.CreateDbContext();
                return (await Orders(db).VerifyBankTransferAsync(orderId, "admin-1", "WIRE-9")).Succeeded;
            });
            var issues = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Issue(pg, id))).ToArray();
            await verify;
            var results = await Task.WhenAll(issues);

            await using var db2 = pg.CreateDbContext();
            var invitations = await db2.Set<AssessmentInvitation>().AsNoTracking().Where(i => i.AssessmentId == id).ToListAsync();
            Assert.True(invitations.Count <= 1);
            Assert.Equal(invitations.Count, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.FailureCode, new[] { "PaymentRequired", "DuplicateActiveInvitation", "IneligibleAssessmentStatus" }));
            var order = await db2.Set<AssessmentOrder>().AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(AssessmentPaymentStatus.Paid, order.PaymentStatus);
            Assert.Equal(AssessmentStatus.Draft == (await db2.Set<Assessment>().AsNoTracking().SingleAsync(a => a.Id == id)).Status, invitations.Count == 0);
        }
        finally { await Cleanup(pg, id); }
    }

    [Fact]
    public async Task Payment_Events_Table_Stores_No_Card_Data_Columns()
    {
        await using var db = new PostgresIntegrationTestFixture().CreateDbContext();
        var names = db.Model.FindEntityType(typeof(AssessmentPaymentEvent))!.GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("card") || n.Contains("pan") || n.Contains("cvv"));
    }

    private sealed class CapturingSender : SentinelMfgSuite.Web.Services.IEmailSender
    {
        public int Count;
        public Task<SentinelMfgSuite.Web.Services.EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            Count++;
            return Task.FromResult(SentinelMfgSuite.Web.Services.EmailSendResult.Accepted("test"));
        }
    }
}
