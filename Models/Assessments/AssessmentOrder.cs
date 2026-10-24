using System;

namespace SentinelMfgSuite.Web.Models.Assessments;

public enum AssessmentPaymentStatus : short
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Cancelled = 3,
    Refunded = 4,
    Disputed = 5
}

public enum AssessmentPaymentMethod : short
{
    BankTransfer = 0,
    PurchaseOrder = 1,
    OnlineProvider = 2
}

public enum AssessmentTaxTreatment : short
{
    /// <summary>No tax is collected (e.g. not registered). Tax amount is always zero.</summary>
    None = 0,

    /// <summary>Tax is added on top of the price using the configured rate.</summary>
    Exclusive = 1
}

public enum AssessmentPaymentEventType : short
{
    OrderCreated = 0,
    PaymentMethodChanged = 1,
    PaymentVerified = 2,
    PaymentFailed = 3,
    OrderCancelled = 4,
    PaymentRefunded = 5,
    PaymentDisputed = 6,
    DisputeResolved = 7,
    PaymentRetryRequested = 8
}

public enum AssessmentPaymentActorType : short
{
    System = 0,
    Administrator = 1,
    Provider = 2
}

/// <summary>
/// Commercial order for a single Sentinel AI Readiness Assessment. Price, tax and total are an immutable
/// snapshot of the configuration in force when the order was created. No card data is ever stored.
/// </summary>
public class AssessmentOrder
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }

    public string CustomerOrganization { get; set; } = string.Empty;
    public string BillingContactName { get; set; } = string.Empty;
    public string BillingContactEmail { get; set; } = string.Empty;

    public string Currency { get; set; } = "CAD";
    public decimal UnitPrice { get; set; }
    public AssessmentTaxTreatment TaxTreatment { get; set; }
    public decimal TaxRate { get; set; }
    public string? TaxLabel { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public AssessmentPaymentStatus PaymentStatus { get; set; } = AssessmentPaymentStatus.Pending;
    public AssessmentPaymentMethod PaymentMethod { get; set; }

    public string? PaymentProvider { get; set; }
    public string? ProviderPaymentReference { get; set; }

    /// <summary>Bank-transfer or other reconciliation reference recorded at verification.</summary>
    public string? PaymentReference { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? VerifiedPaymentUtc { get; set; }
    public string? VerifiedBy { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>Set when a refund or dispute requires an explicit business-policy decision.</summary>
    public bool PolicyReviewRequired { get; set; }
}

/// <summary>Append-only audit record of a payment lifecycle event. Never updated or deleted.</summary>
public class AssessmentPaymentEvent
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public AssessmentPaymentEventType EventType { get; set; }
    public AssessmentPaymentStatus? FromStatus { get; set; }
    public AssessmentPaymentStatus? ToStatus { get; set; }
    public DateTime OccurredUtc { get; set; }
    public AssessmentPaymentActorType ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? Provider { get; set; }
    public string? ProviderEventId { get; set; }
    public string? PaymentReference { get; set; }

    /// <summary>Non-sensitive context only. Never card data, tokens or credentials.</summary>
    public string? Metadata { get; set; }
}
