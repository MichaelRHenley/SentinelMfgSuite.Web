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
        public static EmailSendResult Accepted() => new(EmailSendOutcome.Accepted);

        public static EmailSendResult Suppressed() => new(EmailSendOutcome.Suppressed);
    }

    public interface IEmailSender
    {
        Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody);
    }
}

