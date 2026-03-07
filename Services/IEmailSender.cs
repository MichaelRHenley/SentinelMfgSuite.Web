using System.Threading.Tasks;

namespace SentinelMfgSuite.Web.Services
{
    public interface IEmailSender
    {
        Task SendAsync(string toEmail, string subject, string htmlBody);
    }
}

