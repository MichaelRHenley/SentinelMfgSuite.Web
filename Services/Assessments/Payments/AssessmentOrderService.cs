using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;

namespace SentinelMfgSuite.Web.Services.Assessments.Payments;

public sealed record OrderResult(bool Succeeded, Guid? OrderId, string? FailureCode, string? FailureMessage)
{
    public static OrderResult Ok(Guid id) => new(true, id, null, null);
    public static OrderResult Fail(string code, string message) => new(false, null, code, message);
}

public sealed record InvitationAuthorization(bool Allowed, string Reason);

public sealed record ProviderPaymentEvent(
    string Provider,
    string ProviderEventId,
    AssessmentPaymentEventType EventType,
    string? PaymentReference,
    string? Metadata);

public interface IAssessmentOrderService
{
    Task<OrderResult> CreateOrderAsync(Guid assessmentId, string customerOrganization, string billingContactName, string billingContactEmail, AssessmentPaymentMethod method, string? actorId, CancellationToken ct = default);

    /// <summary>Authenticated administrator reconciles a bank transfer. Requires a non-empty reference.</summary>
    Task<OrderResult> VerifyBankTransferAsync(Guid orderId, string adminUserId, string paymentReference, CancellationToken ct = default);

    Task<OrderResult> CancelAsync(Guid orderId, string adminUserId, string? reason, CancellationToken ct = default);

    /// <summary>Applies an external provider event exactly once. Duplicate provider event ids are ignored.</summary>
    Task<OrderResult> ApplyProviderEventAsync(Guid orderId, ProviderPaymentEvent evt, CancellationToken ct = default);

    Task<OrderResult> RecordRefundAsync(Guid orderId, string adminUserId, string? reference, CancellationToken ct = default);
    Task<OrderResult> RecordDisputeAsync(Guid orderId, string adminUserId, string? reference, CancellationToken ct = default);
}

public interface IInvitationPaymentAuthorizer
{
    Task<InvitationAuthorization> EvaluateAsync(Guid assessmentId, CancellationToken ct = default);
}

/// <summary>
/// Central policy: an invitation may be issued or sent only for LegacyExempt assessments (created before
/// payment enforcement) or when the order is Paid with a recorded verification timestamp. The same rule is
/// also embedded in the atomic SQL conditions of issuance/replacement/delivery so it cannot race.
/// </summary>
public sealed class InvitationPaymentAuthorizer : IInvitationPaymentAuthorizer
{
    private readonly ApplicationDbContext _db;
    public InvitationPaymentAuthorizer(ApplicationDbContext db) => _db = db;

    public async Task<InvitationAuthorization> EvaluateAsync(Guid assessmentId, CancellationToken ct = default)
    {
        var policy = await _db.Set<Assessment>().AsNoTracking()
            .Where(a => a.Id == assessmentId).Select(a => (AssessmentPaymentPolicy?)a.PaymentPolicy).SingleOrDefaultAsync(ct);
        if (policy is null) return new(false, "AssessmentNotFound");
        if (policy == AssessmentPaymentPolicy.LegacyExempt) return new(true, "LegacyExempt");

        var order = await _db.Set<AssessmentOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.AssessmentId == assessmentId, ct);
        if (order is null) return new(false, "NoOrder");
        if (order.PaymentStatus == AssessmentPaymentStatus.Paid && order.VerifiedPaymentUtc is not null) return new(true, "PaidVerified");
        return new(false, $"Payment{order.PaymentStatus}");
    }
}

public sealed class AssessmentOrderService : IAssessmentOrderService
{
    private readonly ApplicationDbContext _db;
    private readonly AssessmentPricingOptions _pricing;

    public AssessmentOrderService(ApplicationDbContext db, IOptions<AssessmentPricingOptions> pricing)
    {
        _db = db;
        _pricing = pricing.Value;
    }

    public static (decimal Tax, decimal Total) ComputeAmounts(AssessmentPricingOptions p)
    {
        var tax = p.TaxTreatment == AssessmentTaxTreatment.Exclusive
            ? Math.Round(p.BasePrice * p.TaxRate, 2, MidpointRounding.AwayFromZero)
            : 0m;
        return (tax, p.BasePrice + tax);
    }

    public async Task<OrderResult> CreateOrderAsync(Guid assessmentId, string customerOrganization, string billingContactName, string billingContactEmail, AssessmentPaymentMethod method, string? actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerOrganization) || string.IsNullOrWhiteSpace(billingContactName) || string.IsNullOrWhiteSpace(billingContactEmail))
            return OrderResult.Fail("InvalidRequest", "Customer organization and billing contact are required.");
        var pricingError = _pricing.Validate();
        if (pricingError is not null) return OrderResult.Fail("PricingNotConfigured", pricingError);

        var assessment = await _db.Set<Assessment>().SingleOrDefaultAsync(a => a.Id == assessmentId, ct);
        if (assessment is null) return OrderResult.Fail("AssessmentNotFound", "Assessment was not found.");
        if (assessment.PaymentPolicy != AssessmentPaymentPolicy.Required)
            return OrderResult.Fail("LegacyAssessment", "Assessments created before payment enforcement do not take orders.");

        var order = StageOrder(_db, _pricing, assessmentId, customerOrganization, billingContactName, billingContactEmail, method, actorId);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return OrderResult.Fail("OrderAlreadyExists", "An order already exists for this assessment.");
        }
        return OrderResult.Ok(order.Id);
    }

    /// <summary>
    /// Adds an order (price/tax snapshot from validated options) and its OrderCreated event to the context
    /// without saving, so callers can persist it atomically with the assessment. Caller must validate options first.
    /// </summary>
    public static AssessmentOrder StageOrder(ApplicationDbContext db, AssessmentPricingOptions pricing, Guid assessmentId, string customerOrganization,
        string billingContactName, string billingContactEmail, AssessmentPaymentMethod method, string? actorId)
    {
        var (tax, total) = ComputeAmounts(pricing);
        var now = DateTime.UtcNow;
        var exclusive = pricing.TaxTreatment == AssessmentTaxTreatment.Exclusive;
        var order = new AssessmentOrder
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            CustomerOrganization = customerOrganization.Trim(),
            BillingContactName = billingContactName.Trim(),
            BillingContactEmail = billingContactEmail.Trim(),
            Currency = "CAD",
            UnitPrice = pricing.BasePrice,
            TaxTreatment = pricing.TaxTreatment,
            TaxRate = exclusive ? pricing.TaxRate : 0m,
            TaxLabel = exclusive ? pricing.TaxLabel : null,
            TaxAmount = tax,
            TotalAmount = total,
            PaymentStatus = AssessmentPaymentStatus.Pending,
            PaymentMethod = method,
            CreatedUtc = now,
            UpdatedUtc = now,
            CreatedBy = actorId
        };
        db.Set<AssessmentOrder>().Add(order);
        db.Set<AssessmentPaymentEvent>().Add(NewEvent(order.Id, AssessmentPaymentEventType.OrderCreated, null, AssessmentPaymentStatus.Pending,
            actorId is null ? AssessmentPaymentActorType.System : AssessmentPaymentActorType.Administrator, actorId, null, null, null, $"method={method}"));
        return order;
    }

    public async Task<OrderResult> VerifyBankTransferAsync(Guid orderId, string adminUserId, string paymentReference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adminUserId)) return OrderResult.Fail("Unauthorized", "An authenticated administrator is required.");
        if (string.IsNullOrWhiteSpace(paymentReference)) return OrderResult.Fail("ReferenceRequired", "A reconciliation reference is required.");

        var order = await _db.Set<AssessmentOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return OrderResult.Fail("OrderNotFound", "Order was not found.");
        if (order.PaymentMethod != AssessmentPaymentMethod.BankTransfer)
            return OrderResult.Fail("MethodNotVerifiable", "Only bank-transfer orders can be reconciled manually.");

        var now = DateTime.UtcNow;
        var reference = paymentReference.Trim();
        var updated = await _db.Set<AssessmentOrder>()
            .Where(o => o.Id == orderId && o.PaymentStatus == AssessmentPaymentStatus.Pending && o.PaymentMethod == AssessmentPaymentMethod.BankTransfer)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.PaymentStatus, AssessmentPaymentStatus.Paid)
                .SetProperty(o => o.VerifiedPaymentUtc, now)
                .SetProperty(o => o.VerifiedBy, adminUserId)
                .SetProperty(o => o.PaymentReference, reference)
                .SetProperty(o => o.UpdatedUtc, now), ct);
        if (updated != 1) return OrderResult.Fail("InvalidTransition", "Only a pending order can be verified.");

        _db.Set<AssessmentPaymentEvent>().Add(NewEvent(orderId, AssessmentPaymentEventType.PaymentVerified, AssessmentPaymentStatus.Pending, AssessmentPaymentStatus.Paid,
            AssessmentPaymentActorType.Administrator, adminUserId, null, null, reference, "reconciliation=bank-transfer"));
        await _db.SaveChangesAsync(ct);
        return OrderResult.Ok(orderId);
    }

    public Task<OrderResult> CancelAsync(Guid orderId, string adminUserId, string? reason, CancellationToken ct = default)
        => TransitionAsync(orderId, AssessmentPaymentEventType.OrderCancelled, AssessmentPaymentStatus.Cancelled,
            new[] { AssessmentPaymentStatus.Pending, AssessmentPaymentStatus.Failed }, AssessmentPaymentActorType.Administrator, adminUserId, null, null, null, reason, false, ct);

    public Task<OrderResult> RecordRefundAsync(Guid orderId, string adminUserId, string? reference, CancellationToken ct = default)
        => TransitionAsync(orderId, AssessmentPaymentEventType.PaymentRefunded, AssessmentPaymentStatus.Refunded,
            new[] { AssessmentPaymentStatus.Paid, AssessmentPaymentStatus.Disputed }, AssessmentPaymentActorType.Administrator, adminUserId, null, null, reference, null, true, ct);

    public Task<OrderResult> RecordDisputeAsync(Guid orderId, string adminUserId, string? reference, CancellationToken ct = default)
        => TransitionAsync(orderId, AssessmentPaymentEventType.PaymentDisputed, AssessmentPaymentStatus.Disputed,
            new[] { AssessmentPaymentStatus.Paid }, AssessmentPaymentActorType.Administrator, adminUserId, null, null, reference, null, true, ct);

    public async Task<OrderResult> ApplyProviderEventAsync(Guid orderId, ProviderPaymentEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.Provider) || string.IsNullOrWhiteSpace(evt.ProviderEventId))
            return OrderResult.Fail("InvalidRequest", "Provider and provider event id are required.");

        var already = await _db.Set<AssessmentPaymentEvent>().AsNoTracking()
            .AnyAsync(e => e.Provider == evt.Provider && e.ProviderEventId == evt.ProviderEventId, ct);
        if (already) return OrderResult.Ok(orderId);

        var (to, from, review) = evt.EventType switch
        {
            AssessmentPaymentEventType.PaymentVerified => (AssessmentPaymentStatus.Paid, new[] { AssessmentPaymentStatus.Pending, AssessmentPaymentStatus.Failed }, false),
            AssessmentPaymentEventType.PaymentFailed => (AssessmentPaymentStatus.Failed, new[] { AssessmentPaymentStatus.Pending }, false),
            AssessmentPaymentEventType.PaymentRefunded => (AssessmentPaymentStatus.Refunded, new[] { AssessmentPaymentStatus.Paid, AssessmentPaymentStatus.Disputed }, true),
            AssessmentPaymentEventType.PaymentDisputed => (AssessmentPaymentStatus.Disputed, new[] { AssessmentPaymentStatus.Paid }, true),
            _ => (AssessmentPaymentStatus.Pending, Array.Empty<AssessmentPaymentStatus>(), false)
        };
        if (from.Length == 0) return OrderResult.Fail("UnsupportedEvent", "Event type is not supported.");

        var order = await _db.Set<AssessmentOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return OrderResult.Fail("OrderNotFound", "Order was not found.");
        if (order.PaymentMethod != AssessmentPaymentMethod.OnlineProvider)
            return OrderResult.Fail("MethodMismatch", "Provider events apply only to online-provider orders.");

        return await TransitionAsync(orderId, evt.EventType, to, from, AssessmentPaymentActorType.Provider, null, evt.Provider, evt.ProviderEventId, evt.PaymentReference, evt.Metadata, review, ct);
    }

    private async Task<OrderResult> TransitionAsync(Guid orderId, AssessmentPaymentEventType type, AssessmentPaymentStatus to, AssessmentPaymentStatus[] allowedFrom,
        AssessmentPaymentActorType actorType, string? actorId, string? provider, string? providerEventId, string? reference, string? metadata, bool policyReview, CancellationToken ct)
    {
        if (actorType == AssessmentPaymentActorType.Administrator && string.IsNullOrWhiteSpace(actorId))
            return OrderResult.Fail("Unauthorized", "An authenticated administrator is required.");

        var current = await _db.Set<AssessmentOrder>().AsNoTracking().Where(o => o.Id == orderId).Select(o => (AssessmentPaymentStatus?)o.PaymentStatus).SingleOrDefaultAsync(ct);
        if (current is null) return OrderResult.Fail("OrderNotFound", "Order was not found.");

        var now = DateTime.UtcNow;
        var verifiedUtc = to == AssessmentPaymentStatus.Paid ? now : (DateTime?)null;
        var updated = await _db.Set<AssessmentOrder>()
            .Where(o => o.Id == orderId && allowedFrom.Contains(o.PaymentStatus))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.PaymentStatus, to)
                .SetProperty(o => o.UpdatedUtc, now)
                .SetProperty(o => o.PolicyReviewRequired, o => o.PolicyReviewRequired || policyReview)
                .SetProperty(o => o.VerifiedPaymentUtc, o => verifiedUtc ?? o.VerifiedPaymentUtc)
                .SetProperty(o => o.ProviderPaymentReference, o => provider != null ? reference ?? o.ProviderPaymentReference : o.ProviderPaymentReference)
                .SetProperty(o => o.PaymentProvider, o => provider ?? o.PaymentProvider), ct);
        if (updated != 1) return OrderResult.Fail("InvalidTransition", $"Order cannot move to {to} from its current status.");

        if (to is AssessmentPaymentStatus.Refunded or AssessmentPaymentStatus.Disputed)
        {
            // Explicit policy: payment is no longer verified, so no NEW invitation can be issued or sent.
            // Already-issued invitations, submitted responses, report revisions and issued PDFs are untouched.
        }

        _db.Set<AssessmentPaymentEvent>().Add(NewEvent(orderId, type, current, to, actorType, actorId, provider, providerEventId, reference, metadata));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
        }
        return OrderResult.Ok(orderId);
    }

    private static AssessmentPaymentEvent NewEvent(Guid orderId, AssessmentPaymentEventType type, AssessmentPaymentStatus? from, AssessmentPaymentStatus? to,
        AssessmentPaymentActorType actorType, string? actorId, string? provider, string? providerEventId, string? reference, string? metadata)
        => new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            EventType = type,
            FromStatus = from,
            ToStatus = to,
            OccurredUtc = DateTime.UtcNow,
            ActorType = actorType,
            ActorId = actorId,
            Provider = provider,
            ProviderEventId = providerEventId,
            PaymentReference = reference,
            Metadata = metadata is { Length: > 1000 } ? metadata[..1000] : metadata
        };
}
