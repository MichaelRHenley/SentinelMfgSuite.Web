using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Options;

namespace SentinelMfgSuite.Web.Services
{
    public class SesEmailSender : IEmailSender
    {
        private readonly EmailSettings _settings;
        private readonly IAmazonSimpleEmailServiceV2 _ses;

        public SesEmailSender(IOptions<EmailSettings> options)
        {
            _settings = options.Value;

                       _ses = new AmazonSimpleEmailServiceV2Client(
                RegionEndpoint.CACentral1);
        }

        public async Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody)
        {
            var request = new SendEmailRequest
            {
                FromEmailAddress = _settings.FromAddress,

                Destination = new Destination
                {
                    ToAddresses = new List<string> { toEmail }
                },

                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content
                        {
                            Data = subject,
                            Charset = "UTF-8"
                        },

                        Body = new Body
                        {
                            Html = new Content
                            {
                                Data = htmlBody,
                                Charset = "UTF-8"
                            }
                        }
                    }
                }
            };

            await _ses.SendEmailAsync(request);
        }
    }
}