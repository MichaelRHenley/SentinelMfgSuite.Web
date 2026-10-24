using System.Threading.Tasks;

namespace SentinelMfgSuite.Web.Services
{
    public enum EmailSendOutcome
    {
        Accepted,
        Suppressed
    }

    public sealed record EmailSendResult(EmailSendOutcome Outcome)
    {
        /// <summary>Provider-assigned id when accepted. Acceptance is not proof of inbox delivery.</summary>
        public string? ProviderMessageId { get; init; }

        public static EmailSendResult Accepted(string? providerMessageId) => new(EmailSendOutcome.Accepted) { ProviderMessageId = providerMessageId };

        public static EmailSendResult Accepted() => new(EmailSendOutcome.Accepted);

        public static EmailSendResult Suppressed() => new(EmailSendOutcome.Suppressed);
    }

    /// <summary>Rendered outbound message. Classification is an operational label only.</summary>
    public sealed record EmailMessage(
        string ToEmail,
        string Subject,
        string HtmlBody,
        string? TextBody = null,
        string Classification = "General",
        string? ReplyToAddress = null);

    public interface IEmailSender
    {
        Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody);

        Task<EmailSendResult> SendAsync(EmailMessage message)
            => SendAsync(message.ToEmail, message.Subject, message.HtmlBody);
    }
}

