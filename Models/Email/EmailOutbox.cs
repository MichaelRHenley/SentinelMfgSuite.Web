namespace SentinelMfgSuite.Web.Models.Email;

public enum EmailOutboxStatus : short
{
    Pending = 0,
    Processing = 1,
    Sent = 2,
    Retry = 3,
    Failed = 4,
    Suppressed = 5
}

/// <summary>
/// Persisted intent to send an email. "Sent" means the provider accepted the message, not that it reached an inbox.
/// </summary>
public class EmailOutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Unique per business event; prevents duplicate messages.</summary>
    public string BusinessEventKey { get; set; } = default!;

    public string Classification { get; set; } = default!;
    public Guid? AssessmentId { get; set; }
    public Guid? InvitationId { get; set; }

    public string RecipientEmail { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string HtmlBody { get; set; } = default!;
    public string TextBody { get; set; } = default!;

    public EmailOutboxStatus Status { get; set; } = EmailOutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextAttemptUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? ProviderAcceptedUtc { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastFailureCode { get; set; }

    public List<EmailOutboxAttempt> Attempts { get; set; } = new();
}

public class EmailOutboxAttempt
{
    public Guid Id { get; set; }
    public Guid OutboxMessageId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? Outcome { get; set; }
    public string? FailureCode { get; set; }
}
