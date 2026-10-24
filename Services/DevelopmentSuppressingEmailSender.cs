using System.Threading.Tasks;

namespace SentinelMfgSuite.Web.Services
{
    /// <summary>
    /// Development-environment email sender that never performs outbound I/O.
    /// It intentionally does not log recipient, subject, body, invitation token,
    /// or invitation URL to avoid leaking sensitive data in local/dev logs.
    /// </summary>
    public class DevelopmentSuppressingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(string toEmail, string subject, string htmlBody)
        {
            return Task.FromResult(EmailSendResult.Suppressed());
        }

        public Task<EmailSendResult> SendAsync(EmailMessage message)
        {
            return Task.FromResult(EmailSendResult.Suppressed());
        }
    }
}
